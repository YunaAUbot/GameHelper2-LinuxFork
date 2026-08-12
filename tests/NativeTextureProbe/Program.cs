using System;
using System.Linq;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Vortice.DXGI;

var assembly = typeof(ClickableTransparentOverlay.Overlay).Assembly;
var type = assembly.GetType("ClickableTransparentOverlay.ImGuiRenderer")
    ?? throw new InvalidOperationException("ImGuiRenderer missing");
var renderer = Activator.CreateInstance(
    type,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    args: new object?[] { null, null, 800, 600, true },
    culture: null) ?? throw new InvalidOperationException("native renderer creation failed");

MethodInfo Method(string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException($"{name} missing");

using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30, 40));
var handle = (IntPtr)(Method("CreateImageTexture").Invoke(renderer, new object[] { image, Format.R8G8B8A8_UNorm })
    ?? throw new InvalidOperationException("texture handle missing"));
var pending = (Array)(Method("GetPendingNativeTextures").Invoke(renderer, null)
    ?? throw new InvalidOperationException("pending textures missing"));
if (pending.Length != 1) throw new InvalidOperationException("new texture was not pending");
var item = pending.GetValue(0) ?? throw new InvalidOperationException("pending texture missing");
var itemType = item.GetType();
var pixels = (byte[])(itemType.GetProperty("Pixels")?.GetValue(item)
    ?? throw new InvalidOperationException("texture pixels missing"));
if (pixels.Length != 16 || !pixels.Take(4).SequenceEqual(new byte[] { 10, 20, 30, 40 }))
    throw new InvalidOperationException("RGBA pixels were not retained exactly");

Method("MarkNativeTextureChunkSent").Invoke(renderer, new object[] { handle, 16 });
Method("AcknowledgeNativeTexture").Invoke(renderer, new object[] { handle, false });
pending = (Array)(Method("GetPendingNativeTextures").Invoke(renderer, null)
    ?? throw new InvalidOperationException("pending textures missing"));
if (pending.Length != 1) throw new InvalidOperationException("rejected texture upload was not retried");
Method("MarkNativeTextureChunkSent").Invoke(renderer, new object[] { handle, 16 });
Method("AcknowledgeNativeTexture").Invoke(renderer, new object[] { handle, true });
pending = (Array)(Method("GetPendingNativeTextures").Invoke(renderer, null)
    ?? throw new InvalidOperationException("pending textures missing"));
if (pending.Length != 0) throw new InvalidOperationException("sent texture remained pending");
if (!(bool)(Method("RemoveImageTexture").Invoke(renderer, new object[] { handle }) ?? false))
    throw new InvalidOperationException("sent texture could not be removed");
var deletes = (long[])(Method("GetPendingNativeTextureDeletes").Invoke(renderer, null)
    ?? throw new InvalidOperationException("pending deletes missing"));
if (deletes.Length != 1 || deletes[0] != handle.ToInt64())
    throw new InvalidOperationException("removed sent texture was not queued for native deletion");
Method("MarkNativeTextureDeleteInFlight").Invoke(renderer, new object[] { deletes[0] });
Method("AcknowledgeNativeTextureDelete").Invoke(renderer, new object[] { deletes[0], false });
deletes = (long[])(Method("GetPendingNativeTextureDeletes").Invoke(renderer, null)
    ?? throw new InvalidOperationException("pending deletes missing"));
if (deletes.Length != 1) throw new InvalidOperationException("rejected texture delete was not retried");
Method("MarkNativeTextureDeleteInFlight").Invoke(renderer, new object[] { deletes[0] });
Method("AcknowledgeNativeTextureDelete").Invoke(renderer, new object[] { deletes[0], true });
deletes = (long[])(Method("GetPendingNativeTextureDeletes").Invoke(renderer, null)
    ?? throw new InvalidOperationException("pending deletes missing"));
if (deletes.Length != 0) throw new InvalidOperationException("acknowledged delete remained pending");

using (var partialImage = new Image<Rgba32>(256, 257, new Rgba32(5, 6, 7, 8)))
{
    var partialHandle = (IntPtr)(Method("CreateImageTexture").Invoke(renderer, new object[] { partialImage, Format.R8G8B8A8_UNorm })
        ?? throw new InvalidOperationException("partial texture handle missing"));
    Method("MarkNativeTextureChunkSent").Invoke(renderer, new object[] { partialHandle, 256 * 1024 });
    if (!(bool)(Method("RemoveImageTexture").Invoke(renderer, new object[] { partialHandle }) ?? false))
        throw new InvalidOperationException("partial texture could not be removed");
    deletes = (long[])(Method("GetPendingNativeTextureDeletes").Invoke(renderer, null)
        ?? throw new InvalidOperationException("pending deletes missing"));
    if (!deletes.Contains(partialHandle.ToInt64()))
        throw new InvalidOperationException("partial native upload was not queued for cleanup");
    Method("MarkNativeTextureDeleteInFlight").Invoke(renderer, new object[] { partialHandle.ToInt64() });
    Method("AcknowledgeNativeTextureDelete").Invoke(renderer, new object[] { partialHandle.ToInt64(), true });
}

var handles = new System.Collections.Generic.List<IntPtr>();
for (var index = 0; index < 64; index++)
{
    using var tiny = new Image<Rgba32>(1, 1, new Rgba32(1, 2, 3, 4));
    handles.Add((IntPtr)(Method("CreateImageTexture").Invoke(renderer, new object[] { tiny, Format.R8G8B8A8_UNorm })
        ?? throw new InvalidOperationException("bounded texture handle missing")));
}
try
{
    using var overflow = new Image<Rgba32>(1, 1, new Rgba32(1, 2, 3, 4));
    _ = Method("CreateImageTexture").Invoke(renderer, new object[] { overflow, Format.R8G8B8A8_UNorm });
    throw new InvalidOperationException("65th native texture was accepted");
}
catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
{
}
foreach (var texture in handles) Method("RemoveImageTexture").Invoke(renderer, new object[] { texture });

((IDisposable)renderer).Dispose();
Console.WriteLine("PASS: native texture pixels, send acknowledgement, and delete lifecycle");
