// Granny Co-op – player model exporter (Unity editor script, NOT part of the mod build).
//
// The game cannot import FBX at runtime, so player models are converted here into the mod's own .gmdl file
// (skinned mesh, texture, bone hierarchy and sampled animations).
//
// Custom character:
//   1. Create (or open) any Unity project, put this file into Assets/Editor/.
//   2. Import your character FBX (Mixamo skeleton: bones named "mixamorig:..."), its texture, and the animation FBX files
//      (Mixamo "Idle", "Walking", "Crouching Idle" – the file names must contain Idle / Walking / Crouch).
//   3. Select the character FBX in the Project window and use  Tools > GrannyMP > Export selected character.
//      The animations are taken from FBX files in the same folder; the texture is the material's main texture or a PNG
//      with the same name as the FBX.
//   4. Copy the .gmdl from <project>/GrannyMP-Export/ to  Granny/BepInEx/plugins/GrannyMP/models/  – every player who
//      should see the character needs the file. The file name is the character's name in the game.
//
// Batch mode (used for the bundled models):  Unity -batchmode -quit -projectPath <p> -executeMethod ModelExport.Run
using System.Collections.Generic; using System.IO; using System.Linq; using UnityEditor; using UnityEngine;

public static class ModelExport {
  static AnimationClip FirstClip(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));

  /// <summary>idle / walk / crouch clips from the animation FBX files in a folder.</summary>
  static List<(string, AnimationClip)> ClipsIn(string folder) {
    var list = new List<(string, AnimationClip)>();
    foreach (var (key, part) in new[] { ("idle", "Idle"), ("walk", "Walking"), ("crouch", "Crouch") }) {
      var file = Directory.GetFiles(folder, "*.fbx").Select(f => f.Replace('\\', '/'))
        .Where(f => Path.GetFileName(f).IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0)
        .OrderBy(f => key == "idle" && Path.GetFileName(f).IndexOf("Crouch", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0).FirstOrDefault();
      var clip = file != null ? FirstClip(file) : null;
      if (clip != null) list.Add((key, clip));
    }
    return list;
  }

  public static void Run() {
    Directory.CreateDirectory("out");
    var clips = ClipsIn("Assets/Src");
    var male = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Src/Character_04.fbx");
    float maleHips = male.transform.Find("mixamorig:Hips").localPosition.y;
    One("Assets/Src/Character_04.fbx", "Assets/Src/Character_04.png", "out/male.gmdl", clips, maleHips);
    One("Assets/Src/Character_Female_10.fbx", "Assets/Src/Character_Female_10.png", "out/female.gmdl", clips, maleHips);
  }

  [MenuItem("Tools/GrannyMP/Export selected character")]
  public static void ExportSelected() {
    var path = AssetDatabase.GetAssetPath(Selection.activeObject);
    if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) { EditorUtility.DisplayDialog("GrannyMP", "Select the character FBX in the Project window.", "OK"); return; }
    var folder = Path.GetDirectoryName(path).Replace('\\', '/');
    var clips = ClipsIn(folder);
    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
    var hips = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.EndsWith("Hips"));
    // the Mixamo animations are made for a hips height of about 2.66 units (Mixamo's own size); the export scales it
    float animHips = 2.66f;
    if (clips.Count > 0) {
      var animModel = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(clips[0].Item2));
      var ah = animModel ? animModel.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.EndsWith("Hips")) : null;
      if (ah) animHips = Mathf.Max(0.01f, ah.localPosition.y);
    }
    var png = Path.ChangeExtension(path, ".png");
    Directory.CreateDirectory("GrannyMP-Export");
    var outPath = "GrannyMP-Export/" + Path.GetFileNameWithoutExtension(path) + ".gmdl";
    One(path, File.Exists(png) ? png : null, outPath, clips, animHips);
    EditorUtility.DisplayDialog("GrannyMP", $"Exported {outPath}\nAnimations: {string.Join(", ", clips.Select(c => c.Item1))}" + (hips ? "" : "\n(no Hips bone – the model is not animated)"), "OK");
  }

  static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
  static void W(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }

  static Texture2D ReadableTexture(string png, SkinnedMeshRenderer smr) {
    var t = new Texture2D(2, 2);
    if (png != null) { t.LoadImage(File.ReadAllBytes(png)); return t; }
    var main = smr.sharedMaterial ? smr.sharedMaterial.mainTexture as Texture2D : null;
    if (main == null) { t.SetPixel(0, 0, Color.gray); t.Apply(); return t; }
    // copy through a render texture – works also for textures that are not readable
    var rt = RenderTexture.GetTemporary(main.width, main.height, 0);
    Graphics.Blit(main, rt);
    var prev = RenderTexture.active; RenderTexture.active = rt;
    t = new Texture2D(main.width, main.height, TextureFormat.RGBA32, false);
    t.ReadPixels(new Rect(0, 0, main.width, main.height), 0, 0); t.Apply();
    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
    return t;
  }

  static void One(string fbx, string png, string outPath, List<(string, AnimationClip)> clips, float animHips) {
    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
    var inst = Object.Instantiate(asset);
    var nodes = inst.GetComponentsInChildren<Transform>(true).ToList(); // [0] = root
    var idx = new Dictionary<Transform, int>(); for (int i = 0; i < nodes.Count; i++) idx[nodes[i]] = i;
    var restP = nodes.Select(t => t.localPosition).ToArray(); var restR = nodes.Select(t => t.localRotation).ToArray(); var restS = nodes.Select(t => t.localScale).ToArray();
    var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
    var mesh = smr.sharedMesh;
    var hips = nodes.FindIndex(t => t.name.EndsWith("Hips"));
    float hipsScale = hips >= 0 ? restP[hips].y / animHips : 1f;
    using var w = new BinaryWriter(File.Create(outPath));
    w.Write(0x4C444D47); w.Write(1); // "GMDL" v1
    w.Write(nodes.Count);
    for (int i = 0; i < nodes.Count; i++) { w.Write(nodes[i].name); w.Write(i == 0 ? -1 : idx[nodes[i].parent]); W(w, restP[i]); W(w, restR[i]); W(w, restS[i]); }
    // mesh (in the space of the skinned renderer's node)
    w.Write(idx[smr.transform]); w.Write(smr.rootBone ? idx[smr.rootBone] : System.Math.Max(0, hips));
    w.Write(smr.bones.Length); foreach (var b in smr.bones) w.Write(idx[b]);
    foreach (var m in mesh.bindposes) for (int k = 0; k < 16; k++) w.Write(m[k]);
    var v = mesh.vertices; var n = mesh.normals; var uv = mesh.uv; var bw = mesh.boneWeights;
    w.Write(v.Length);
    for (int i = 0; i < v.Length; i++) { W(w, v[i]); W(w, n.Length == v.Length ? n[i] : Vector3.up); w.Write(uv.Length == v.Length ? uv[i].x : 0f); w.Write(uv.Length == v.Length ? uv[i].y : 0f);
      var b = bw[i]; w.Write(b.boneIndex0); w.Write(b.boneIndex1); w.Write(b.boneIndex2); w.Write(b.boneIndex3); w.Write(b.weight0); w.Write(b.weight1); w.Write(b.weight2); w.Write(b.weight3); }
    w.Write(mesh.subMeshCount);
    for (int s = 0; s < mesh.subMeshCount; s++) { var tri = mesh.GetTriangles(s); w.Write(tri.Length); foreach (var t in tri) w.Write(t); }
    // texture: RGBA32 rows bottom to top, at most 512 px
    var src = ReadableTexture(png, smr);
    int tw = Mathf.Min(512, src.width), th = Mathf.Min(512, src.height);
    w.Write(tw); w.Write(th);
    for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) { Color32 c = src.GetPixelBilinear((x + 0.5f) / tw, (y + 0.5f) / th); w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
    // animations: rotation of every node + the hips position, 30 frames per second
    w.Write(hips >= 0 ? clips.Count : 0);
    if (hips >= 0)
      foreach (var (name, clip) in clips) {
        const float fps = 30f; int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * fps) + 1);
        w.Write(name); w.Write(fps); w.Write(frames);
        for (int f = 0; f < frames; f++) {
          for (int i = 0; i < nodes.Count; i++) { nodes[i].localPosition = restP[i]; nodes[i].localRotation = restR[i]; }
          clip.SampleAnimation(inst, Mathf.Min(clip.length, f / fps));
          for (int i = 0; i < nodes.Count; i++) W(w, nodes[i].localRotation);
          var hp = nodes[hips].localPosition;
          // walking in place: no forward drift, the height scaled to this model's legs
          W(w, new Vector3(restP[hips].x, hp.y * hipsScale, restP[hips].z));
        }
      }
    Debug.Log($"exported {outPath}: {nodes.Count} nodes, {v.Length} verts, {smr.bones.Length} bones, tex {tw}x{th}, {clips.Count} animations");
    Object.DestroyImmediate(inst);
  }
}
