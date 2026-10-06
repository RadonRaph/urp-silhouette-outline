# Silhouette Outline

Blurred silhouette outline for URP. Depth tested, XR compatible.

## How it works

1. Copy the scene depth.
2. Draw outlined objects as flat colors, depth tested against that copy.
3. Blur the mask vertically then horizontally.
4. Subtract the silhouette, leaving a ring.
5. Blend the ring over the camera color.

## Requirements

- Unity 6000.3+
- URP 17.3+ with Render Graph (Compatibility Mode not supported)

## Install

Package Manager > `+` > *Install package from git URL*:

```
https://github.com/RadonRaph/urp-silhouette-outline.git
```

Then add the **Silhouette Outline** Renderer Feature to your Universal Renderer Data.

## Usage

Add an `OutlineTarget` component to a GameObject. Its `MeshRenderer` and `SkinnedMeshRenderer` (children included) are outlined while the component is enabled.

```csharp
var target = go.AddComponent<OutlineTarget>();
target.Color = Color.yellow;
```

Call `RefreshRenderers()` after changing the hierarchy at runtime. With a `LODGroup`, only LOD 0 is outlined.

Or draw meshes directly:

```csharp
using SilhouetteOutline;

// Persistent
int handle = OutlineManager.AddInstruction(new OutlineInstruction(mesh, matrix, Color.cyan));
OutlineManager.UpdateInstruction(handle, new OutlineInstruction(mesh, newMatrix, Color.cyan));
OutlineManager.RemoveInstruction(handle);

// Current frame only
OutlineManager.AddFrameInstruction(new OutlineInstruction(mesh, matrices, colors));
```

## Settings

| Setting | Description |
| --- | --- |
| Render Pass Event | Default `BeforeRenderingPostProcessing`. |
| Blur Range | Outline thickness, in pixels. |
| Depth Bias / Slope Depth Bias | Slack for the silhouette depth test. |
| Occlude Outline | Hide the outline where the scene is in front of the object. Costs extra texture reads and three R32F targets. |
| Occlusion Tolerance | How far (world units) the scene can be in front before the outline fades. Keeps contact edges visible. |

## XR

Supports single pass instanced, multiview and multi pass. Not yet tested on device.

Blur Range is in pixels, so headsets usually need a higher value.
