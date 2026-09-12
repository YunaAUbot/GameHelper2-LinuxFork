using GameHelper;
using GameHelper.RemoteObjects.States.InGameStateObjects;
using GameOffsets.Natives;
using GameOffsets.Objects.States.InGameState;
void Check(bool ok,string why){if(!ok)throw new Exception(why);}
var reader=Core.Process.Handle;
var area=new AreaInstance{Address=(IntPtr)100,AreaHash="one"};
reader.Data[area.Address]=new AreaInstanceOffsets{Entities=new EntityListStruct{SleepingEntities=new StdMap{Head=(IntPtr)200,Size=5000}}};
reader.Data[(IntPtr)200]=new StdMapNode<EntityNodeKey,EntityNodeValue>{Parent=(IntPtr)1000};
for(int i=0;i<5000;i++){
 var ep=(IntPtr)(10000+i);var dp=(IntPtr)(20000+i);
 reader.Data[(IntPtr)(1000+i)]=new StdMapNode<EntityNodeKey,EntityNodeValue>{Right=i==4999?(IntPtr)1000:(IntPtr)(1001+i),Data=new(){Value=new(){EntityPtr=ep}}};
 reader.Data[ep]=new EntityOffsets{Id=(uint)i,ItemBase=new(){EntityDetailsPtr=dp}};
 reader.Data[dp]=new EntityDetails();reader.Paths[ep]=i==4500?"wanted":"other";
 reader.DetailPaths[dp]=reader.Paths[ep];
}
var scanner=new SleepingEntityScanner(area);var found=new List<uint>();
var before=reader.Reads;scanner.ScanNext(p=>p=="wanted",e=>found.Add(e.Id),default);
Check(reader.Reads-before<10000,"Unbounded first batch");Check(found.Count==0,"Did not yield before distant target");
for(int i=0;i<100 && found.Count==0;i++)scanner.ScanNext(p=>p=="wanted",e=>found.Add(e.Id),default);
Check(found.SequenceEqual(new uint[]{4500}),"Lost target across batches");Check(Entity.Created==1,"Constructed rejected entities");
area.AreaHash="two";before=reader.Reads;scanner.ScanNext(_=>true,_=>throw new Exception("old area callback"),default);Check(reader.Reads==before,"Old area read");
using var cts=new CancellationTokenSource();cts.Cancel();
try{scanner.ScanNext(_=>true,_=>{},cts.Token);throw new Exception("Missing cancellation");}catch(OperationCanceledException){}
Console.WriteLine("PASS: incremental 5000-node scan, cyclic tree, filter before construction, area identity, cancellation");
namespace GameHelper {
 public static class Core {public static FakeProcess Process {get;}=new();}
 public class FakeProcess {public FakeReader Handle {get;}=new();}
 public class FakeReader {
  public Dictionary<IntPtr,object> Data=new();public Dictionary<IntPtr,string> Paths=new();public Dictionary<IntPtr,string> DetailPaths=new();public int Reads;private IntPtr lastDetails;
  public bool TryReadMemory<T>(IntPtr address,out T value) where T:struct {Reads++;if(typeof(T)==typeof(EntityDetails))lastDetails=address;if(Data.TryGetValue(address,out var item)&&item is T result){value=result;return true;}value=default;return false;}
  public string ReadStdWString(StdWString value)=>DetailPaths[lastDetails];
 }
}
namespace GameHelper.Utils {public static class SafeMemoryHandle{public static bool IsValidAddress(IntPtr p)=>p.ToInt64()>0;}}
namespace GameHelper.RemoteObjects.States.InGameStateObjects {
 public class AreaInstance{public IntPtr Address;public string AreaHash="";}
 public class Entity {public static int Created;public uint Id;public string Path;public Entity(IntPtr p){Created++;Core.Process.Handle.TryReadMemory<EntityOffsets>(p,out var x);Id=x.Id;Path=Core.Process.Handle.Paths[p];}}
}
