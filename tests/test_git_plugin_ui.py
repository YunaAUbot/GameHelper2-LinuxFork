#!/usr/bin/env python3
"""Compile the production cached revision formatter without a renderer or network."""
import os,pathlib,subprocess,tempfile
ROOT=pathlib.Path(__file__).resolve().parents[1]
text=(ROOT/'GameHelper/Plugin/GitPluginInstaller.cs').read_text()
def block(signature):
 start=text.index(signature); pos=text.index('{',start); end=pos+1; depth=1
 while depth:
  depth+=(text[end]=='{')-(text[end]=='}');end+=1
 return text[start:end]
source=block('private sealed class Source')
methods=block('private static string FormatRevision')+'\n'+block('internal static string RevisionLabel')+'\n'+block('internal static string RevisionDetail')
with tempfile.TemporaryDirectory() as tmp:
 p=pathlib.Path(tmp)
 (p/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><NoWarn>0649</NoWarn></PropertyGroup></Project>')
 (p/'Program.cs').write_text('''using System; using System.Linq; using System.Threading;
static class OverlayLocalization {
 public static string T(string key,string fallback)=>fallback;
 public static string F(string key,string fallback,params object[] args)=>string.Format(fallback,args);
}
static class Probe {
 static Source[] sources=Array.Empty<Source>(); static bool inventoryReadable;
'''+source+methods+'''
 static void Equal(string expected,string actual) {if(expected!=actual)throw new Exception($"Expected {expected}, got {actual}");}
 static void Main() {
  Equal("Unknown",RevisionLabel("NinjaPricer")); inventoryReadable=true;
  Equal("Local",RevisionLabel("NinjaPricer")); Equal("Local",RevisionLabel("LootValue"));
  var s=new Source {name="NinjaPricer"};sources=new[]{s};
  Equal("Unknown",RevisionLabel(s.name));
  s.revisionState="current"; Equal("Up to date",RevisionLabel(s.name));
  s.revisionState="behind";s.behind=3; Equal("3 commits behind",RevisionLabel(s.name));
  s.behind=-1; Equal("Unknown",RevisionLabel(s.name));
  s.revisionState="ahead"; Equal("Ahead",RevisionLabel(s.name));
  s.revisionState="diverged"; Equal("Diverged",RevisionLabel(s.name));
  s.revisionState="unknown";s.checkedAt=1; Equal("Unknown",RevisionLabel(s.name));
  Equal("Last verified check: 1970-01-01 00:00 UTC",RevisionDetail(s.name));
  s.checkedAt=long.MaxValue; Equal("",RevisionDetail(s.name));
  sources=new[]{s,s}; Equal("Unknown",RevisionLabel(s.name));
  inventoryReadable=false;sources=Array.Empty<Source>();Equal("Unknown",RevisionLabel("LootValue"));
  Console.WriteLine("PASS: 14 production revision UI assertions");
 }
}''')
 subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project',str(p),'-c','Release'],check=True)

# Exercise the production per-row/all controls with deterministic ImGui clicks.
controls=block('internal static void DrawRevisionUpdate')+'\n'+block('private static void DrawUpdateAll')+'\n'+block('private static bool HasSource')+'\n'+block('private static bool IsRepositoryUrl')
with tempfile.TemporaryDirectory() as tmp:
 p=pathlib.Path(tmp)
 (p/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><NoWarn>0649</NoWarn></PropertyGroup></Project>')
 (p/'Program.cs').write_text('''using System; using System.Linq; using System.Threading;
static class OverlayLocalization { public static string Label(string k,string f,string id)=>f; }
static class ImGui {
 static bool disabled; public static void BeginDisabled(bool b)=>disabled=b; public static void EndDisabled()=>disabled=false;
 public static bool Button(string s)=>!disabled; public static void SameLine(){} public static void TextUnformatted(string s){} public static void TextWrapped(string s){}
}
static class Probe {
 static Source[] sources=Array.Empty<Source>(); static bool inventoryReadable=true,trust=true; static int busy;
 static System.Collections.Generic.List<object> requests=new(); static void Submit(object r)=>requests.Add(r);
'''+source+controls+'''
 static void Main(){
 foreach(var state in new[]{"behind","unknown","ahead","diverged","current"}) {
 sources=new[]{new Source{id="a",name="Fixture",url="https://github.com/a/b",revisionState=state}};
 DrawRevisionUpdate("Fixture"); }
 if(requests.Count!=5)throw new Exception("Per-plugin updates did not queue");
 DrawUpdateAll();if(requests.Count!=6)throw new Exception("All did not queue");
 for(int i=0;i<requests.Count;i++) {
 var request=requests[i];var action=request.GetType().GetProperty("action").GetValue(request);
 if(!Equals(action,i==5?"update-all":"update"))throw new Exception("Wrong action");
 }
 trust=false;DrawRevisionUpdate("Fixture");DrawUpdateAll();
 trust=true;DrawRevisionUpdate("Local");sources=Array.Empty<Source>();DrawUpdateAll();
 if(requests.Count!=6)throw new Exception("Untrusted/local update queued");
 Console.WriteLine("PASS: production per/all controls queue and enforce source/trust");
 }
}''')
 subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project',str(p),'-c','Release'],check=True)

# The actual file bridge returns immediately and writes its bounded mailbox on Task.Run.
with tempfile.TemporaryDirectory() as tmp:
 p=pathlib.Path(tmp)
 (p/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
 (p/'Program.cs').write_text('''using System; using System.IO; using System.Threading; using System.Threading.Tasks;
using Newtonsoft.Json;
namespace Newtonsoft.Json {static class JsonConvert {public static string SerializeObject(object o)=>System.Text.Json.JsonSerializer.Serialize(o);}}
static class OverlayLocalization {public static string T(string k,string f)=>f;}
static class Probe {
 static CancellationTokenSource Lifetime=new();static string Home=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 static int busy;static string status="";
'''+block('private static void Submit')+'''
 static async Task Main(){
 Environment.SetEnvironmentVariable("GAMEHELPER2_GIT_WORKER","1");
 try {
 Submit(new {action="update-all",trust=true});
 while(Volatile.Read(ref busy)!=0)await Task.Delay(10);
 var path=Path.Combine(Home,"request.json");var original=await File.ReadAllTextAsync(path);
 if(!original.Contains("update-all"))throw new Exception("No queued batch");
 Submit(new {action="update",id="another",trust=true});
 while(Volatile.Read(ref busy)!=0)await Task.Delay(10);
 if(await File.ReadAllTextAsync(path)!=original)throw new Exception("Pending request overwritten");
 if(!status.Contains("pending"))throw new Exception("Missing pending feedback");
 Console.WriteLine("PASS: production asynchronous mailbox writes batch and preserves bounded pending request");
 }finally {if(Directory.Exists(Home))Directory.Delete(Home,true);}
 }
}''')
 subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project',str(p),'-c','Release'],check=True,timeout=30)

# Actual background reader is folder-authoritative even with stale status files.
with tempfile.TemporaryDirectory() as tmp:
 p=pathlib.Path(tmp)
 (p/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><NoWarn>0649</NoWarn></PropertyGroup></Project>')
 (p/'Program.cs').write_text('''using System; using System.IO; using System.Linq; using System.Threading; using System.Threading.Tasks; using Newtonsoft.Json;
namespace Newtonsoft.Json {
 class JsonException:Exception{}
 static class JsonConvert {public static T DeserializeObject<T>(string s)=>System.Text.Json.JsonSerializer.Deserialize<T>(s,new System.Text.Json.JsonSerializerOptions{IncludeFields=true});}
}
static class Probe {
 static CancellationTokenSource Lifetime=new(); static Source[] sources=Array.Empty<Source>();
'''+source+block('private static async Task<Source[]> ReadFolderSnapshot')+block('internal static bool FolderPresent')+'''
 static async Task Main(){
 var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());Directory.CreateDirectory(root);
 try {
 File.WriteAllText(Path.Combine(root,"sources.json"),"[{\\"name\\":\\"Ghost\\"}]");
 sources=await ReadFolderSnapshot(Path.Combine(root,"Plugins"));if(sources.Length!=0)throw new Exception("Registry supplied inventory");
 var folder=Path.Combine(root,"Plugins","Fixture");Directory.CreateDirectory(folder);Directory.CreateDirectory(Path.Combine(folder,".git"));
 File.WriteAllText(Path.Combine(folder,".git-status.json"),"{\\"name\\":\\"WrongName\\",\\"url\\":\\"https://example.invalid/a/b\\",\\"revisionState\\":\\"behind\\",\\"behind\\":2}");
 sources=await ReadFolderSnapshot(Path.Combine(root,"Plugins"));if(!FolderPresent("Fixture")||FolderPresent("WrongName")||sources[0].behind!=2)throw new Exception("Folder identity lost");
 Directory.Delete(Path.Combine(folder,".git"));sources=await ReadFolderSnapshot(Path.Combine(root,"Plugins"));
 if(sources[0].revisionState!="local"||sources[0].url!="")throw new Exception("Stale status supplied remote");
 Directory.Delete(folder,true);sources=await ReadFolderSnapshot(Path.Combine(root,"Plugins"));
 if(FolderPresent("Fixture")||sources.Length!=0)throw new Exception("Deleted folder still visible");
 Console.WriteLine("PASS: production background folder reader ignores stale registry/status and drops deleted folders");
 }finally{Directory.Delete(root,true);}
 }
}''')
 subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project',str(p),'-c','Release'],check=True,timeout=30)
