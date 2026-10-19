using System.IO;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Build Broken Showcase
    ///
    /// Builds a viewable copy of the glove carrying the faults a person can actually see,
    /// so the asset checks can be demonstrated against something on screen rather than
    /// against a report alone.
    ///
    /// It deliberately does *not* import the broken kit's textures. Those include a 4096
    /// map and a non-power-of-two one, and bringing all thirty-four into the project would
    /// add tens of megabytes to a web build in order to show faults that leave no visible
    /// trace. The authored textures are reused instead, with two slots emptied, and the
    /// report carries the rest.
    /// </summary>
    public static class BrokenShowcaseBuilder
    {
        private const string Kit = "pipeline/demo/broken_glove/GloveAsset/falcon_glove.obj";
        private const string Folder = "Assets/RecastCustomizer/Resources";
        private const string MeshAsset = Folder + "/falcon_glove_broken.obj";
        public const string PrefabName = "falcon_glove_broken_p";
        private const string PrefabAsset = Folder + "/" + PrefabName + ".prefab";
        private const string MatFolder = "Assets/RecastCustomizer/Demo";

        // child name -> which mesh inside the broken OBJ it should take
        private static readonly (string child, string mesh)[] Parts =
        {
            ("top", "falcon_glove_top"),
            ("bottom", "textureEditorIsolateSelectSet_falcon_glove_bottom"),
            ("thumb", "falcon_glove_thumb"),
            ("tassel", "falcon_glove_tassel"),
        };

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        [MenuItem("Recast Customizer/Build Broken Showcase")]
        public static void Build()
        {
            var source = Path.Combine(ProjectRoot, Kit);
            if (!File.Exists(source))
            {
                EditorUtility.DisplayDialog("Broken showcase",
                    "The broken kit has not been generated yet.\n\nRun:\n" +
                    "blender --background --python pipeline/demo/make_broken_glove.py",
                    "OK");
                return;
            }

            Directory.CreateDirectory(Path.Combine(ProjectRoot, Folder));
            Directory.CreateDirectory(Path.Combine(ProjectRoot, MatFolder));
            File.Copy(source, Path.Combine(ProjectRoot, MeshAsset), true);
            AssetDatabase.ImportAsset(MeshAsset, ImportAssetOptions.ForceUpdate);
            MatchImportScale();

            var meshes = new System.Collections.Generic.Dictionary<string, Mesh>();
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(MeshAsset))
                if (obj is Mesh m) meshes[m.name] = m;

            // Materials are made first, then referred to by path rather than by the
            // instance that created them. Saving and importing invalidates those
            // instances, which is why two parts kept coming out with no material: the
            // object assigned to the renderer had already been replaced underneath it.
            var materialPaths = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var (child, _) in Parts)
                materialPaths[child] = MaterialPathFor(child);
            AssetDatabase.SaveAssets();
            // Deliberately no Refresh here. Delete, create and refresh in the same frame
            // leaves the new path briefly unloadable, and the load below then returns
            // null -- which is how two parts kept ending up with no material.

            var root = new GameObject("falcon_glove_broken");
            int bound = 0, unbound = 0;

            foreach (var (child, meshName) in Parts)
            {
                var go = new GameObject(child);
                go.transform.SetParent(root.transform, false);
                var filter = go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();

                // The bottom's group carries a stray selection-set name. It is bound here
                // anyway, by the name the export actually gave it, so the copy looks whole:
                // a version that announces itself as broken teaches nobody anything. The
                // name is still wrong, and the checks still report it.
                if (meshes.TryGetValue(meshName, out var mesh))
                {
                    filter.sharedMesh = mesh;
                    bound++;
                }
                else
                {
                    unbound++;
                }

                renderer.sharedMaterial =
                    AssetDatabase.LoadAssetAtPath<Material>(materialPaths[child]);
            }

            // Save over a clean slate. Rebuilding a material deletes and recreates it,
            // which gives it a new GUID, and SaveAsPrefabAsset merges into whatever is
            // already at the path -- so the previous prefab's now-dangling reference won
            // and two parts came back with no material at all.
            AssetDatabase.DeleteAsset(PrefabAsset);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabAsset);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[Recast] Broken showcase built: " + bound + " part(s) bound, "
                      + unbound + " left unbound. " + PrefabAsset);
            Selection.activeObject = saved;
        }

        /// <summary>
        /// Import the copy exactly as the authored glove is imported.
        ///
        /// The authored mesh comes in at a scale factor of 0.01 because its file units are
        /// inches. A fresh import defaults to 1, which makes the copy a hundred times too
        /// big -- large enough that the camera sits inside it and nothing appears to be
        /// there at all.
        /// </summary>
        private static void MatchImportScale()
        {
            var authored = AssetImporter.GetAtPath(
                "Assets/RecastCustomizer/GloveAsset/falcon_glove.obj") as ModelImporter;
            var copy = AssetImporter.GetAtPath(MeshAsset) as ModelImporter;
            if (authored == null || copy == null) return;

            copy.globalScale = authored.globalScale;
            copy.useFileScale = authored.useFileScale;
            copy.useFileUnits = authored.useFileUnits;
            copy.importNormals = authored.importNormals;
            copy.SaveAndReimport();

            Debug.Log("[Recast] Broken showcase imported at scale "
                      + copy.globalScale + ", matching the authored glove.");
        }

        /// <summary>
        /// The authored material, or a damaged copy of it where the fault needs to show.
        /// </summary>
        private static string MaterialPathFor(string child)
        {
            string sourcePath =
                "Assets/RecastCustomizer/GloveAsset/falcon_glove_" + child + "_01_mat.mat";
            var authored = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (authored == null) return null;

            // One material fault, on the thumb: its own normal map plugged into the colour
            // slot. Everyone has done it, it is unmistakable once pointed at, and it is
            // easy to miss on a small part. Every other part keeps its authored material.
            //
            // The restraint is the point. A copy carrying visible damage everywhere would
            // be sent back before anyone ran a check on it, which is not what a validator
            // is for: it earns its keep on what gets through review, not on what does not.
            if (child != "thumb") return sourcePath;

            string path = MatFolder + "/falcon_glove_broken_" + child + "_mat.mat";
            var broken = new Material(authored) { name = "falcon_glove_broken_" + child + "_mat" };

            var normal = AssetDatabase.LoadAssetAtPath<Texture>(
                "Assets/RecastCustomizer/GloveAsset/falcon_glove_thumb_01_norm.png");
            if (normal != null) broken.SetTexture("_BaseMap", normal);

            // Creating the folder on disk is not enough: AssetDatabase.CreateAsset fails
            // silently into a folder it has not imported, which left the parts with no
            // material at all.
            if (!AssetDatabase.IsValidFolder(MatFolder))
            {
                AssetDatabase.Refresh();
                if (!AssetDatabase.IsValidFolder(MatFolder))
                    AssetDatabase.CreateFolder("Assets/RecastCustomizer", "Demo");
            }

            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(broken, path);
            AssetDatabase.SaveAssets();
            return path;
        }
    }
}
