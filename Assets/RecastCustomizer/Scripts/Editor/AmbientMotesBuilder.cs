using System.IO;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Build Ambient Motes
    ///
    /// Creates a particle system of slow glowing motes sitting behind the glove.
    ///
    /// These used to be drawn as a UI overlay, which had two problems that could not be
    /// solved on that layer: an overlay always draws in front of the 3D view, and its specks
    /// sit at a fixed screen size no matter where the camera is. Putting them in the scene
    /// fixes both at once — they are genuinely behind the asset, and perspective scales the
    /// whole field as the camera dollies, exactly as it scales the glove.
    ///
    /// Re-run to rebuild. Safe to tweak by hand afterwards; the tool only replaces the
    /// object it made.
    /// </summary>
    public static class AmbientMotesBuilder
    {
        private const string ObjectName = "Ambient Motes";
        private const string TextureFolder = "Assets/RecastCustomizer/Textures";
        private const string TexturePath = TextureFolder + "/mote_dot.png";
        private const string MaterialPath = TextureFolder + "/Recast_Mote.mat";

        [MenuItem("Recast Customizer/Build Ambient Motes")]
        public static void Build()
        {
            var assembly = Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null)
            {
                Debug.LogError("[AmbientMotes] No ModularGloveAssembly in the open scene. " +
                               "Open glove_studio and try again.");
                return;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[AmbientMotes] No camera tagged MainCamera.");
                return;
            }

            // measure the glove so the field is sized to it rather than to arbitrary numbers
            var renderers = assembly.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogError("[AmbientMotes] The assembly has no renderers to measure.");
                return;
            }
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            float span = Mathf.Max(bounds.size.magnitude, 0.01f);

            var existing = GameObject.Find(ObjectName);
            if (existing != null) Undo.DestroyObjectImmediate(existing);

            var go = new GameObject(ObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Build ambient motes");

            // Sit the emitter volume behind the glove, along the camera's view direction, so
            // every mote renders behind the asset from this angle.
            var back = cam.transform.forward;
            go.transform.position = bounds.center + back * (span * 0.85f);
            go.transform.rotation = cam.transform.rotation;

            var ps = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            // Long lives and very low speeds: these are meant to hang in the air behind
            // the glove, not swarm. Anything faster reads as insects and pulls the eye off
            // the asset, which is the opposite of what a backdrop element is for.
            main.startLifetime = new ParticleSystem.MinMaxCurve(40f, 70f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(span * 0.003f, span * 0.010f);
            main.startSize = new ParticleSystem.MinMaxCurve(span * 0.008f, span * 0.030f);

            // A wide brightness spread is what stops a particle field reading as a flat
            // sheet of dots: the dim end sits back in the haze, the bright end catches the
            // eye. The gold is pushed more saturated than the panels so it reads as light.
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.86f, 0.44f, 0.34f),
                new Color(1.00f, 0.74f, 0.20f, 1.00f));
            main.maxParticles = 190;
            main.gravityModifier = 0f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);

            var emission = ps.emission;
            emission.rateOverTime = 5f;
            // A burst on the first frame so the field is already ~70% populated when the
            // screen opens. Without it the trickle takes the better part of a minute to
            // fill, and the opening shot looks empty.
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)Mathf.RoundToInt(190 * 0.72f))
            });

            // a slab wider than it is deep — reads as a field of air, not a cloud
            // Far wider than the glove, so motes carry right out to the edges of frame
            // rather than clustering behind the asset. The screen vignette handles the
            // falloff at the borders, so the emitter itself stays an even field.
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(span * 7.0f, span * 4.6f, span * 2.2f);

            // One slow shared drift, so the field breathes in a single direction instead
            // of each mote wandering on its own.
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-span * 0.004f, span * 0.004f);
            vel.y = new ParticleSystem.MinMaxCurve(span * 0.002f, span * 0.008f);
            vel.z = new ParticleSystem.MinMaxCurve(-span * 0.002f, span * 0.002f);

            // Noise kept very low and very slow — just enough that paths are not straight
            // lines. The previous values were an order of magnitude too strong.
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = span * 0.008f;
            noise.frequency = 0.02f;
            noise.scrollSpeed = 0.005f;
            noise.damping = true;

            // fade in and out so motes never pop
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    // born at 70% rather than from nothing, so nothing is seen to switch on
                    new GradientAlphaKey(0.70f, 0f),
                    new GradientAlphaKey(1f, 0.14f),
                    new GradientAlphaKey(1f, 0.80f),
                    new GradientAlphaKey(0f, 1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var moteMat = EnsureMaterial();
            renderer.material = moteMat;

            if (moteMat.mainTexture == null)
                Debug.LogWarning("[AmbientMotes] The mote material has no texture — motes " +
                                 "will render as hard squares. Delete " + MaterialPath +
                                 " and run this again.");
            renderer.sortingOrder = -10;
            renderer.alignment = ParticleSystemRenderSpace.View;

            // prime it so the field is already populated rather than filling in on play
            ps.Simulate(55f, true, true);
            ps.Play();

            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            Selection.activeObject = go;
            Debug.Log($"[AmbientMotes] Built '{ObjectName}' behind the glove.\n" +
                      $"  Field spans {span * 7f:0.0} units, 190 motes, 40-70s lifetimes, " +
                      "burst-filled to ~70% on the first frame.\n" +
                      "  Perspective scales them with the camera automatically.");
        }

        private static Material EnsureMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat != null) return mat;

            Directory.CreateDirectory(TextureFolder);

            // Sprites/Default first: it is unlit, alpha-blended, honours _MainTex and
            // behaves predictably under URP. The URP particle shader needs keyword setup
            // that is easy to get subtly wrong, and a mis-set one renders untextured quads —
            // which is exactly the hard-edged squares this replaces.
            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Particles/Unlit");
            mat = new Material(shader) { name = "Recast_Mote" };

            var dot = EnsureDotTexture();

            // assign every slot a candidate shader might read from
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", dot);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", dot);
            mat.mainTexture = dot;

            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 2f);       // additive
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            mat.renderQueue = 3000;
            mat.color = Color.white;

            AssetDatabase.CreateAsset(mat, MaterialPath);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>A soft round dot: opaque centre falling to nothing at the rim.</summary>
        private static Texture2D EnsureDotTexture()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (existing != null) return existing;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * a;               // tight core, long falloff
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(TexturePath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(TexturePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }
    }
}
