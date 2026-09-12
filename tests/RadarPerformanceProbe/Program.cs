using System.Numerics;
using Radar;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var grid = Enumerable.Repeat((byte)0x11, 32 * 64).ToArray();
var doors = new HashSet<(int,int)> {(-1,0),(64,0),(10,10)};
foreach (var (x,y) in new[] {(-1,0),(64,0),(0,-1),(0,64),(int.MaxValue,1)})
    Check(!LineWalker.IsWalkable(grid,32,x,y,doors), "Raster bounds");
Check(!LineWalker.IsWalkable(grid,0,0,0), "Zero stride");
var random = new Random(42);
for (int i=0;i<grid.Length;i++) grid[i]=(byte)random.Next(256);
for (int i=0;i<20000;i++) {
    var a=new Vector2(random.Next(64),random.Next(64)); var b=new Vector2(random.Next(64),random.Next(64));
    var counted=LineWalker.CheckLine(grid,32,a,b,doors);
    Check(counted.IsClear==LineWalker.IsLineClear(grid,32,a,b,doors), "Line visibility changed");
    Check(counted.TotalCells==(int)Math.Max(Math.Abs(a.X-b.X),Math.Abs(a.Y-b.Y))+1, "Line count");
}
Array.Fill(grid,(byte)0x11);
for(int y=0;y<64;y++) grid[y*32+16]=0;
Check(Pathfinder.FindPath(grid,32,new(4,4),new(50,4))==null,"Disconnected wall");
doors=new();for(int x=32;x<34;x++)doors.Add((x,30));
var route=Pathfinder.FindPath(grid,32,new(4,4),new(50,4),doors);
Check(route is {Count:>1},"Door bridge");
for(int i=1;i<route!.Count;i++) Check(LineWalker.IsLineClear(grid,32,route[i-1],route[i],doors),"Invalid path segment");
using var cancelled = new CancellationTokenSource();cancelled.Cancel();
try {Pathfinder.FindPath(grid,32,new(4,4),new(50,4),cancellationToken:cancelled.Token);throw new Exception("Missing cancellation");}catch(OperationCanceledException){}
try {Pathfinder.SmoothPath(grid,32,route,cancellationToken:cancelled.Token);throw new Exception("Missing smoothing cancellation");}catch(OperationCanceledException){}
var cache=new PathWorkCache<int>();
async Task PumpUntil(Func<bool> condition) {
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while(!condition()){deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(5);cache.Pump();}
}
int calls=0;
List<Vector2>? Fail(Vector2 p,List<Vector2>? old,int seg,CancellationToken ct){Interlocked.Increment(ref calls);return null;}
cache.Schedule(new[]{(1,new Vector2(1,1))},Vector2.Zero,0,3,3000,Fail);
await PumpUntil(()=>cache.Paths.ContainsKey(1));
cache.Schedule(new[]{(1,new Vector2(1,1))},Vector2.Zero,0,3,3000,Fail);
await Task.Delay(30);cache.Pump();Check(calls==1,"Negative cache retry");
cache.Schedule(new[]{(1,new Vector2(1,1))},Vector2.Zero,1,3,3000,Fail);
await PumpUntil(()=>calls==2);await Task.Delay(20);cache.Pump();
var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
cache.Reset();
cache.Schedule(new[]{(2,new Vector2(2,2))},Vector2.Zero,0,3,3000,(p,old,seg,ct)=>{started.SetResult();ct.WaitHandle.WaitOne();ct.ThrowIfCancellationRequested();return null;});
await started.Task.WaitAsync(TimeSpan.FromSeconds(5));cache.Reset();
cache.Schedule(new[]{(3,new Vector2(3,3))},Vector2.Zero,0,3,3000,(p,old,seg,ct)=>new(){p});
await PumpUntil(()=>cache.Paths.ContainsKey(3));Check(!cache.Paths.ContainsKey(2),"Stale area result published");
cache.Reset();
var targets=Enumerable.Range(0,13).Select(i=>(i,new Vector2(i,i))).ToArray();
for(int i=0;i<20 && cache.Paths.Count<13;i++){
 cache.Schedule(targets,Vector2.Zero,0,3,3000,(p,old,seg,ct)=>new(){p});await Task.Delay(15);cache.Pump();
}
Check(cache.Paths.Count==13,"Far targets starved");cache.Reset();
Console.WriteLine("PASS: bounds, 20000 lines, disconnected paths, doors, cancellation, retry invalidation, area isolation, fair batches");

var entities=new System.Collections.Concurrent.ConcurrentDictionary<int,object>();
for(int i=0;i<10000;i++)entities[i]=new object();
long Walk(bool snapshot){long n=0;if(snapshot){foreach(var v in entities.Values)n++;}else{foreach(var kv in entities)n++;}return n;}
Walk(true);Walk(false);
long Alloc(bool snapshot){long start=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<100;i++)Walk(snapshot);return GC.GetAllocatedBytesForCurrentThread()-start;}
Console.WriteLine($"100 scans of 10000 entities: Values allocated {Alloc(true)} bytes; direct enumeration {Alloc(false)} bytes");
