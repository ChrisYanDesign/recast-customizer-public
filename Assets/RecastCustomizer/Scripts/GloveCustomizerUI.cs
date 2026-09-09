using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecastCustomizer
{
    /// <summary>
    /// Equipment screen for the falconer's glove.
    ///
    /// The framing is a game outfitting menu, not a material browser: the glove is the
    /// character's essential gear, and each material trades one advantage for another.
    /// Changing a design shifts the attribute readout, and that movement is the point.
    ///
    /// Layout:
    ///   top centre    screen tabs
    ///   top-left      identity — name, subtitle, tier
    ///   top-right     technical readout, deliberately quiet
    ///   bottom-left   attributes with emblems, animated on change
    ///   bottom-right  every segment's materials, with the design presets beneath them
    ///   centre        the glove, one callout per segment naming what is fitted
    ///
    /// On any change the screen answers in one orchestrated moment: the swatch lifts, the
    /// badge pops, its leader line redraws, the attribute bars ease to their new values and
    /// the camera dips a fraction and settles. That half-second is what separates an
    /// equipment screen from a settings panel.
    ///
    /// Everything is generated from <see cref="GloveVariantSet"/> at runtime, so adding a
    /// segment, a variant or an attribute grows the screen with no edits here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GloveCustomizerUI : MonoBehaviour
    {
        /// <summary>
        /// True while the cursor sits over a control. OrbitCamera reads this so scrolling
        /// or dragging on the interface does not also move the 3D view underneath it.
        /// </summary>
        public static bool PointerOverUI { get; private set; }

        [SerializeField] private ModularGloveAssembly assembly;
        [SerializeField] private string title = "Falcon Glove";
        [Tooltip("Leave empty to hide the line under the title.")]
        [SerializeField] private string subtitle = "";
        [Tooltip("Heading for the figures beneath it. 'MASTER' said nothing about what " +
                 "follows; this labels the block instead of decorating it.")]
        [SerializeField] private string tier = "ASSET DATA";

        [Header("Type")]
        [Tooltip("Display face. Cinzel ships in Assets/RecastCustomizer/Fonts.")]
        [SerializeField] private Font displayFont;
        [SerializeField] private bool serifThroughout = true;

        [Header("Sizing")]
        [Tooltip("Scales the whole interface — type and geometry together — so the panels " +
                 "keep their proportions at any window size. 1 is the original scale.")]
        [Range(0.3f, 2f)]
        [SerializeField] private float uiScale = 0.6f;

        [Range(0.5f, 3f)]
        [SerializeField] private float textScale = 1.35f;

        [Tooltip("Width of a material preview before uiScale is applied.")]
        [SerializeField] private float swatchWidth = 34f;
        [Tooltip("Width of the fitted material preview, as a multiple of Swatch Width. " +
                 "Height is deliberately not derived from this: the panel is wider than " +
                 "it is tall, so previews grow sideways only.")]
        [SerializeField] private float swatchCentreWide = 1.86f;
        [Tooltip("Width of the two stepper chips flanking the preview. Kept narrow so the " +
                 "preview gets the room; they are a control, not a picture.")]
        [SerializeField] private float swatchSideWide = 0.52f;
        [SerializeField] private float presetWidth = 56f;

        [Tooltip("Minimum width of the materials panel before uiScale. The panel grows past " +
                 "this if its content needs more, so the frame always contains the rows.")]
        [SerializeField] private float materialsWidth = 426.7f;
        [Tooltip("Minimum width of the top-left identity plate. It sizes to its content " +
                 "otherwise, which leaves the technical figures cramped against the frame. " +
                 "Set to match the attributes plate below it, so the two left-hand panels " +
                 "line up rather than reading as two different widths.")]
        [SerializeField] private float identityWidth = 426.7f;

        [Header("Inspection")]
        [Tooltip("Show the channel-isolation row (albedo, normal, smoothness, UV, wireframe) " +
                 "above the control legend. Off by default: the modes work, but they are a " +
                 "standard feature of every look-dev tool and they crowd a screen whose point " +
                 "is the material review loop. The code stays in the project either way -- " +
                 "tick this to bring the row back.")]
        [SerializeField] private bool showViewModes = false;

        [Header("Guide")]
        [Tooltip("Open the walkthrough every time the tool starts, not just once. " +
                 "Right for a portfolio piece: almost every visitor is a first-time " +
                 "visitor, and a guide they never see does no work. Skip dismisses it, and " +
                 "GUIDE in the legend brings it back.")]
        [SerializeField] private bool alwaysShowGuide = true;

        [Tooltip("Padding inside every plate, before uiScale. Keep it generous or the " +
                 "corner scrollwork on the frame will run into the text.")]
        [SerializeField] private float platePadding = 34f;

        [Header("Callouts")]
        [SerializeField] private bool showCallouts = true;
        [SerializeField] private float calloutClearance = 74f;
        [SerializeField] private float calloutSpacing = 140f;
        [SerializeField] private float calloutMargin = 18f;
        [Tooltip("Bearing of each callout around the asset, in degrees. " +
                 "0 = right, 90 = up, 180 = left, 270 = down. One per segment.")]
        [SerializeField] private float[] calloutAngles = { 155f, 325f, 35f, 215f };
        [Range(0f, 1f)]
        [SerializeField] private float calloutAnchorBias = 0.30f;
        [SerializeField] private float badgeSize = 52f;

        [Header("Framing")]
        [Range(0f, 1f)]
        [SerializeField] private float vignetteStrength = 0.55f;
        [Header("Motion")]
        [Range(0.05f, 2f)]
        [SerializeField] private float statSettleTime = 0.45f;
        [Range(0.1f, 1f)]
        [SerializeField] private float popTime = 0.34f;

        [Header("Export")]
        [SerializeField] private string screenshotFolder = "Screenshots";

        // ---- palette: warm brass on near-black ----
        private static readonly Color Trim       = new(0.95f, 0.80f, 0.30f, 0.92f);
        private static readonly Color PlateFill  = new(0.050f, 0.045f, 0.034f, 0.92f);
        private static readonly Color ChipBg     = new(0.12f, 0.11f, 0.09f, 1f);
        private static readonly Color ChipBorder = new(0.48f, 0.43f, 0.30f, 0.60f);
        private static readonly Color TextMain   = new(0.97f, 0.94f, 0.84f, 1f);
        private static readonly Color TextMuted  = new(0.68f, 0.63f, 0.51f, 1f);
        /// <summary>
        /// Warm parchment for readouts. Plain white reads as a system dialogue dropped on
        /// top of the art; this sits near the brass of the frames without matching it, so
        /// the figures belong to the panel they are printed on.
        /// </summary>
        private static readonly Color Parchment  = new(0.94f, 0.87f, 0.66f, 1f);
        private static readonly Color Gold       = new(0.96f, 0.82f, 0.34f, 1f);
        private static readonly Color Track      = new(1f, 0.95f, 0.78f, 0.14f);

        /// <summary>Per-segment accents. Kept distinct so callouts can be told apart.</summary>
        private static readonly Color[] Accents =
        {
            new(0.98f, 0.80f, 0.30f, 1f),   // gold
            new(0.44f, 0.82f, 0.90f, 1f),   // verdigris
            new(0.84f, 0.52f, 0.90f, 1f),   // amethyst
            new(0.56f, 0.90f, 0.62f, 1f),   // jade
        };

        /// <summary>
        /// Attributes share one warm metal ramp rather than a different hue each. Five
        /// saturated colours in a column read as a chart; brass at five values reads as a
        /// set of struck emblems, which is what the rest of the screen is doing.
        /// </summary>
        private static readonly Color[] AttributeTones =
        {
            new(0.96f, 0.84f, 0.46f, 1f),   // pale gold
            new(0.88f, 0.72f, 0.36f, 1f),   // brass
            new(0.80f, 0.63f, 0.30f, 1f),   // old brass
            new(0.72f, 0.56f, 0.28f, 1f),   // bronze
            new(0.64f, 0.50f, 0.26f, 1f),   // dark bronze
        };

        private static Color ToneFor(int i) => AttributeTones[i % AttributeTones.Length];

        private const float BaseTitle   = 15f;
        private const float BaseSub     = 7f;
        private const float BaseSection = 8f;
        private const float BaseBig     = 17f;
        private const float BaseHint    = 7.5f;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void RecastDownloadFile(string filename, byte[] data, int length);
#endif

        private sealed class Callout
        {
            public VisualElement Badge, LabelBox;
            public Label Value;
            public Transform Anchor;
            public Vector2 BadgeCentre, AnchorPoint;
            public bool Visible;
            public float Pop;          // 1 -> 0 after a change
            public float Reveal = 1f;  // 0 -> 1 line redraw
        }

        private sealed class StatRow
        {
            public VisualElement Fill;
            public Label Value;
            public float Shown, Target;
        }

        private sealed class KnobRow
        {
            public MaterialTuner.Knob Knob;
            public float Min, Max;
            public VisualElement Fill;
            public VisualElement Track;
            public VisualElement Handle;
            public float HandleSize;
            public Label Value;
        }

        /// <summary>One material row rendered as a three-slot carousel.</summary>
        private sealed class MaterialRow
        {
            public VisualElement Left, Centre, Right;
            public Label Value;
            public Label Name;
        }

        private readonly List<Callout> _callouts = new();
        private readonly List<StatRow> _stats = new();
        private readonly List<Button> _presetTiles = new();
        private readonly List<MaterialRow> _matRows = new();
        private readonly List<VisualElement> _bottomAnchored = new();
        private readonly List<KnobRow> _knobs = new();
        private MaterialTuner _tuner;
        private ViewModeController _views;
        private readonly List<Button> _viewChips = new();
        private VisualElement _hintBar;
        private VisualElement _viewBar;
        private VisualElement _identityPlate, _materialsPlate, _designPlate, _tunerBlock, _exportChip;
        private VisualElement _materialRowsBlock, _tunerBlockFull;
        private CustomizerTour _tour;
        private int _focused;               // which segment the sliders are editing
        private Label _tunerTitle;
        private Label _costRow;

        private Renderer[] _segmentRenderers;
        private VisualElement _root;
        private VisualElement _lineLayer;
        private Camera _cam;
        private OrbitCamera _orbit;

        /// <summary>
        /// Resolved on demand rather than once at startup. Startup order is not guaranteed --
        /// if the camera is disabled or the domain is reloading at that moment, a one-shot
        /// lookup stores null for the rest of the session and the camera dip silently stops
        /// happening, with nothing in the console to say why.
        /// </summary>
        private OrbitCamera Orbit
        {
            get
            {
                if (_orbit != null) return _orbit;
                if (_cam == null) _cam = Camera.main;
                if (_cam != null) _orbit = _cam.GetComponent<OrbitCamera>();
                return _orbit;
            }
        }

        private int[] _lastSelection;
        private bool _applyingPreset;


        /// <summary>Font size, scaled.</summary>
        private float S(float v) => v * textScale * uiScale;

        /// <summary>A pixel dimension, scaled. Everything laid out goes through this so the
        /// interface shrinks and grows as one piece rather than drifting out of proportion.</summary>
        private float U(float px) => px * uiScale;
        private static Color AccentFor(int i) => Accents[i % Accents.Length];

        // ---------------------------------------------------------------- lifecycle

        private void OnEnable()
        {
            if (assembly == null) assembly = FindFirstObjectByType<ModularGloveAssembly>();
            _cam = Camera.main;
            BuildPanel();
            if (assembly != null) assembly.SelectionChanged += OnSelectionChanged;
        }

        private void OnDisable()
        {
            if (_tuner != null) _tuner.Changed -= RefreshTuner;

            if (assembly != null) assembly.SelectionChanged -= OnSelectionChanged;
            PointerOverUI = false;
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled && Application.isPlaying) BuildPanel();
        }

        private void Update()
        {
            TrackPointer();
            if (Input.GetKeyDown(KeyCode.R)) assembly.Randomize();
            if (Input.GetKeyDown(KeyCode.S)) StartCoroutine(CaptureScreenshot());
            if (Input.GetKeyDown(KeyCode.G) && _tour != null && !_tour.IsRunning) _tour.Start();
            AnimateStats();
            AnimatePops();
        }

        private void LateUpdate()
        {
            if (showCallouts) UpdateCallouts();
        }

        private void TrackPointer()
        {
            if (_root == null || _root.panel == null) { PointerOverUI = false; return; }
            var panelPos = RuntimePanelUtils.ScreenToPanel(_root.panel, Input.mousePosition);
            var picked = _root.panel.Pick(panelPos);
            PointerOverUI = picked != null && picked != _root && picked.pickingMode != PickingMode.Ignore;
        }

        /// <summary>
        /// One orchestrated response to a change: pop the badges whose segment moved, redraw
        /// their leader lines, and ask the camera for a small dip.
        /// </summary>
        private void OnSelectionChanged()
        {
            // Only the segment that actually moved should answer. Redrawing every leader
            // line when one thumb material changed reads as noise, not feedback.
            if (_lastSelection != null)
            {
                for (int s = 0; s < _callouts.Count && s < assembly.Selection.Count; s++)
                {
                    if (s >= _lastSelection.Length || _lastSelection[s] == assembly.Selection[s]) continue;
                    _callouts[s].Pop = 1f;
                    _callouts[s].Reveal = 0f;
                }
            }
            else
            {
                foreach (var c in _callouts) { c.Pop = 1f; c.Reveal = 0f; }
            }

            _lastSelection = new int[assembly.Selection.Count];
            for (int i = 0; i < _lastSelection.Length; i++) _lastSelection[i] = assembly.Selection[i];

            // The camera only dips for a whole-outfit change; a single material swap is
            // answered by its own badge, and moving the whole view for it would be too much.
            if (!_applyingPreset && _orbit != null) { /* no breath on a single swap */ }

            RefreshAll();
        }

        // ---------------------------------------------------------------- build

        private void BuildPanel()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.Clear();
            _root.pickingMode = PickingMode.Ignore;
            _callouts.Clear();
            _stats.Clear();
            _presetTiles.Clear();
            _matRows.Clear();
            _bottomAnchored.Clear();
            _knobs.Clear();
            _viewChips.Clear();
            if (_views == null && assembly != null)
                _views = assembly.GetComponent<ViewModeController>();
            if (_tuner == null && assembly != null)
            {
                _tuner = assembly.GetComponent<MaterialTuner>();
                // Follow the tuner rather than only the sliders, so the readouts stay honest
                // when something else changes a value -- a reset, or a preset swap.
                if (_tuner != null) _tuner.Changed += RefreshTuner;
            }
            _lastSelection = null;

            var set = assembly != null ? assembly.VariantSet : null;
            if (set == null)
            {
                var warn = new Label("No GloveVariantSet assigned.");
                warn.style.color = TextMain;
                _root.Add(warn);
                return;
            }

            BuildVignette();
            BuildLineLayer();
            if (showCallouts) BuildCallouts(set);

            BuildNavBar();
            BuildIdentity();
            BuildDesignCorner(set);
            BuildOutfitCorner(set);
            BuildViewModes();
            BuildHints();

            RefreshAll(instant: true);
            BuildTour();
        }

        private CustomizerChrome.OrnateFrame Plate(bool left, bool top)
        {
            var f = new CustomizerChrome.OrnateFrame { Trim = Trim, Fill = PlateFill };
            f.Cut = U(15f);
            f.Inset = U(5f);
            f.style.position = Position.Absolute;
            if (left) f.style.left = 0f; else f.style.right = 0f;
            if (top) f.style.top = 0f; else { f.style.bottom = U(46f); _bottomAnchored.Add(f); }
            f.style.paddingLeft = U(platePadding);
            f.style.paddingRight = U(platePadding);
            f.style.paddingTop = U(platePadding * 0.85f);
            f.style.paddingBottom = U(platePadding * 0.85f);
            _root.Add(f);
            return f;
        }

        private void ApplyFont(Label l, bool display)
        {
            if (displayFont == null) return;
            if (display || serifThroughout)
                l.style.unityFontDefinition = FontDefinition.FromFont(displayFont);
        }

        /// <summary>The screen tabs. Only one is live, but the row is what frames this as a
        /// screen inside a game rather than a floating tool window.</summary>
        /// <summary>
        /// The screen tabs, set in an ornate strip so the top of the frame answers the
        /// control legend at the bottom. Only one tab is live; the row exists to frame this
        /// as a screen inside a game rather than a floating tool window.
        /// </summary>
        /// <summary>
        /// The screen tabs. Deliberately unframed — a hard box here would fight the corner
        /// plates and crowd the top of the view. The live tab carries the weight instead,
        /// through size, colour and a ruled underline with diamond terminals.
        /// </summary>
        private void BuildNavBar()
        {
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.top = 16;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.justifyContent = Justify.Center;
            bar.style.alignItems = Align.FlexStart;
            bar.pickingMode = PickingMode.Ignore;
            _root.Add(bar);

            string[] tabs = { "QUARRY", "MEWS", "GLOVE", "HOOD", "FIELD" };
            const int live = 2;

            for (int i = 0; i < tabs.Length; i++)
            {
                bool on = i == live;

                var wrap = new VisualElement();
                wrap.style.alignItems = Align.Center;
                wrap.style.marginLeft = U(34f);
                wrap.style.marginRight = U(34f);
                wrap.pickingMode = PickingMode.Ignore;

                var l = new Label(tabs[i]);
                l.style.color = on ? Gold : new Color(TextMuted.r, TextMuted.g, TextMuted.b, 0.50f);
                l.style.fontSize = S(BaseSection) * (on ? 1.85f : 1.25f);
                l.style.letterSpacing = U(on ? 3.2f : 2.2f);
                if (on) l.style.unityFontStyleAndWeight = FontStyle.Bold;
                ApplyFont(l, true);
                wrap.Add(l);

                if (on) wrap.Add(TabUnderline(S(BaseSection) * 11f));

                bar.Add(wrap);
            }
        }

        /// <summary>A ruled underline with a diamond at each end and one at the centre.</summary>
        private VisualElement TabUnderline(float width)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = U(7f);
            row.pickingMode = PickingMode.Ignore;

            row.Add(Diamond(4f));

            var line = new VisualElement();
            line.style.height = 1;
            line.style.width = width;
            line.style.marginLeft = U(5f);
            line.style.marginRight = U(5f);
            line.style.backgroundColor = Gold;
            row.Add(line);

            row.Add(Diamond(4f));
            return row;
        }

        private VisualElement Diamond(float size)
        {
            var d = new VisualElement();
            d.style.width = size;
            d.style.height = size;
            d.style.backgroundColor = Gold;
            d.style.rotate = new Rotate(45f);
            d.pickingMode = PickingMode.Ignore;
            return d;
        }

        /// <summary>
        /// Top-left: the asset's identity, with the technical figures folded in beneath the
        /// tier chip. They were a separate plate; merging them frees the top-right corner
        /// for the materials and stops three panels competing along the top edge.
        /// </summary>
        private void BuildIdentity()
        {
            var c = Plate(left: true, top: true);
            _identityPlate = c;
            c.style.minWidth = U(identityWidth);

            var name = new Label(Spaced(title));
            name.style.color = TextMain;
            name.style.fontSize = S(BaseTitle);
            name.style.letterSpacing = U(2.2f);
            name.style.wordSpacing = U(9f);
            ApplyFont(name, true);
            c.Add(name);

            var sub = new Label(subtitle);
            sub.style.display = string.IsNullOrWhiteSpace(subtitle)
                ? DisplayStyle.None : DisplayStyle.Flex;
            sub.style.color = TextMuted;
            sub.style.fontSize = S(BaseSub);
            sub.style.letterSpacing = U(1.6f);
            sub.style.wordSpacing = U(6f);
            sub.style.marginTop = U(4f);
            ApplyFont(sub, false);
            c.Add(sub);

            var chip = new Label(Spaced(tier));
            chip.style.color = Gold;
            chip.style.fontSize = S(BaseSection) * 1.05f;
            chip.style.letterSpacing = U(1.4f);
            // Letter spacing without word spacing closes the gap between words: every
            // character gets pushed apart by the same amount including the space, so the
            // space stops being wider than the gaps around it and "ASSET DATA" reads as one
            // word. Every other label here sets both; this one had been missing the pair.
            chip.style.wordSpacing = U(12f);
            chip.style.marginTop = U(10f);
            chip.style.paddingLeft = U(9f);
            chip.style.paddingRight = U(9f);
            chip.style.paddingTop = U(2f);
            chip.style.paddingBottom = U(3f);
            chip.style.alignSelf = Align.FlexStart;
            SetBorderWidth(chip, 1);
            SetBorderColor(chip, new Color(Gold.r, Gold.g, Gold.b, 0.5f));
            ApplyFont(chip, false);
            c.Add(chip);

            // ---- technical figures, quiet, beneath the tier ----
            var rule = new VisualElement();
            rule.style.height = 1;
            rule.style.marginTop = U(12f);
            rule.style.marginBottom = U(10f);
            rule.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.22f);
            c.Add(rule);

            int tris = 0, maxMap = 0, mats = 0;
            foreach (var mf in assembly.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    tris += (int)(mesh.GetIndexCount(sm) / 3);
            }
            foreach (var r in assembly.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial == null) continue;
                mats++;
                if (r.sharedMaterial.mainTexture != null)
                    maxMap = Mathf.Max(maxMap, r.sharedMaterial.mainTexture.width);
            }

            // Written for someone who has never seen the tool. The old labels were trade
            // shorthand: "MAPS" and "MATERIALS" are only obvious if you already work in this
            // field, and "COMBINATIONS" named a number without saying combinations of what.
            var rows = new (string value, string label)[]
            {
                (tris.ToString("n0"), "TRIS"),
                (maxMap + " PX",      "TEXTURE SIZE"),
                (mats.ToString(),     "SWAPPABLE PARTS"),
                (assembly.VariantSet.CombinationCount.ToString(), "DESIGN COMBINATIONS")
            };
            foreach (var row in rows) c.Add(FigureRow(row.value, row.label, Parchment));
            BuildCostRow(c);
        }

        /// <summary>
        /// One figure: its number and what the number means, as two elements side by side.
        ///
        /// These used to be a single string with spaces in it, and the spaces kept vanishing.
        /// Letter spacing widens every glyph gap including the space, so a space stops being
        /// wider than the gaps inside a word and the text closes up. Laying the two parts out
        /// as separate elements with a real margin between them removes the problem at the
        /// source instead of fighting the font's metrics.
        /// </summary>
        private VisualElement FigureRow(string value, string label, Color colour)
        {
            var line = new VisualElement();
            line.style.flexDirection = FlexDirection.Row;
            line.style.alignItems = Align.Center;
            line.style.marginBottom = U(5f);

            var v = new Label(Spaced(value));
            v.style.color = colour;
            v.style.fontSize = S(BaseSection) * 1.18f;
            v.style.letterSpacing = U(0.9f);
            v.style.unityFontStyleAndWeight = FontStyle.Bold;
            v.style.marginRight = U(14f);
            v.style.flexShrink = 0;
            ApplyFont(v, false);
            line.Add(v);

            var l = new Label(Spaced(label));
            l.style.color = new Color(colour.r, colour.g, colour.b, 0.80f);
            l.style.fontSize = S(BaseSection) * 1.05f;
            l.style.letterSpacing = U(0.9f);
            l.style.wordSpacing = U(14f);
            ApplyFont(l, false);
            line.Add(l);

            return line;
        }

        /// <summary>Adds the live texture-memory figure beneath the static ones.</summary>
        private void BuildCostRow(VisualElement c)
        {
            // The four figures above describe the whole asset and never move. This one is
            // the cost of the combination currently equipped, and it changes on every swap:
            // variants are not all equally expensive, so a browsing artist can see when a
            // pairing is heavy without leaving the tool.
            // A rule above it: the four figures above describe the asset and never move,
            // this one changes every time a material is swapped. Grouping them without a
            // break invites the reader to assume all five behave the same way.
            var liveRule = new VisualElement();
            liveRule.style.height = 1;
            liveRule.style.marginTop = U(7f);
            liveRule.style.marginBottom = U(7f);
            liveRule.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.16f);
            c.Add(liveRule);

            // Same two-part row as the figures above, so its spacing behaves the same way.
            // Only the number changes at runtime, so only the number is kept a reference to.
            var line = FigureRow("0.0 MB", "TEXTURE MEMORY", Gold);
            _costRow = line[0] as Label;
            c.Add(line);

            UpdateCostRow();
        }

        /// <summary>
        /// Texture memory for the materials currently equipped, in megabytes.
        ///
        /// Textures are gathered into a set first because variants share maps — every
        /// bottom variant points at the same occlusion map, and counting it once per
        /// segment would inflate the figure well past what the asset actually costs.
        /// </summary>
        private void UpdateCostRow()
        {
            if (_costRow == null || assembly == null || assembly.VariantSet == null) return;

            var seen = new HashSet<Texture>();
            long bytes = 0;
            var set = assembly.VariantSet;

            for (int s = 0; s < set.segments.Count && s < assembly.Selection.Count; s++)
            {
                var mat = set.segments[s].VariantMaterial(assembly.Selection[s]);
                if (mat == null || mat.shader == null) continue;

                // These live on the Shader object itself. The similarly named ShaderUtil is
                // editor-only, so reaching for it would compile in the editor and then fail
                // the moment this went into a player.
                int count = mat.shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (mat.shader.GetPropertyType(i)
                        != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;

                    var tex = mat.GetTexture(mat.shader.GetPropertyNameId(i));
                    if (tex == null || !seen.Add(tex)) continue;

                    // The profiler is the accurate source, but it can report nothing in a
                    // non-development build, which would leave the row reading 0.0 MB on the
                    // web. Fall back to an uncompressed estimate so the figure stays useful.
                    long size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
                    if (size <= 0) size = (long)tex.width * tex.height * 4L;
                    bytes += size;
                }
            }

            // Not "this combination". The row above already counts how many combinations
            // exist, so reusing the word made one line look like a broken version of the
            // other. This measures something different -- the texture memory the equipped
            // materials actually occupy -- so it says that instead.
            _costRow.text = Spaced((bytes / 1048576f).ToString("0.0") + " MB");
        }

        /// <summary>
        /// Bottom-left: the five values a reviewer actually asks to change, for whichever
        /// segment is focused. This replaced a panel of invented falconry statistics. Those
        /// proved the data pipeline worked but told an artist nothing they could act on --
        /// a reading of 66 does not say whether the roughness or the occlusion caused it.
        /// </summary>
        private void BuildTuner(VisualElement c, GloveVariantSet set)
        {
            if (_tuner == null || set.segments.Count == 0) return;

            // Not "MATERIAL" again -- the list above is already the materials, and repeating
            // the word says nothing about what these five controls do. "LOOK" names the
            // thing being adjusted and matches the EXPORT LOOK button these feed into, so
            // the panel and the button read as one workflow.
            var lookHead = Centred(SectionLabel("LOOK DEV"));
            c.Add(lookHead);
            _tunerBlock = lookHead;

            var knobBlock = new VisualElement();
            knobBlock.style.width = Length.Percent(100f);
            c.Add(knobBlock);
            _tunerBlockFull = knobBlock;
            c = knobBlock;

            _tunerTitle = new Label();
            _tunerTitle.style.color = Gold;
            _tunerTitle.style.fontSize = S(BaseSection);
            _tunerTitle.style.letterSpacing = U(1.4f);
            _tunerTitle.style.wordSpacing = U(4f);
            _tunerTitle.style.marginBottom = U(10f);
            ApplyFont(_tunerTitle, false);
            c.Add(_tunerTitle);

            for (int i = 0; i < MaterialTuner.Knobs.Length; i++)
            {
                var spec = MaterialTuner.Knobs[i];
                var accent = ToneFor(i);

                // Full width on purpose. Without it the row shrink-wraps its content, and a
                // flexible track has no free space to grow into, so it lands at zero pixels
                // wide and the slider is invisible.
                var line = new VisualElement();
                line.style.flexDirection = FlexDirection.Column;
                line.style.width = Length.Percent(100f);
                line.style.marginBottom = U(10f);

                var head = new VisualElement();
                head.style.flexDirection = FlexDirection.Row;
                head.style.alignItems = Align.Center;
                head.style.width = Length.Percent(100f);
                line.Add(head);

                var nm = new Label(spec.label);
                nm.style.color = Parchment;
                nm.style.fontSize = S(BaseSection) * 1.12f;
                nm.style.letterSpacing = U(1.2f);
                nm.style.wordSpacing = U(4f);
                nm.style.flexGrow = 1f;
                ApplyFont(nm, false);
                head.Add(nm);

                // A bare element rather than a UI Toolkit Slider: the stock one brings its own
                // theme, and stripping that back to match this chrome is more work than
                // drawing a track and handling the drag directly.
                // The track is the hit area, not just the line. A 6px bar is close to
                // impossible to catch with a mouse, so the element is made tall enough to
                // grab and the visible groove is drawn inside it.
                var track = new VisualElement();
                track.style.height = Mathf.Max(14f, U(22f));
                track.style.flexGrow = 1f;
                track.style.width = Length.Percent(100f);
                track.style.justifyContent = Justify.Center;
                line.Add(track);

                var groove = new VisualElement();
                groove.style.height = Mathf.Max(3f, U(5f));
                groove.style.backgroundColor = Track;
                track.Add(groove);

                var fill = new VisualElement();
                fill.style.height = Length.Percent(100f);
                fill.style.width = Length.Percent(50f);
                fill.style.backgroundColor = accent;
                groove.Add(fill);

                // A brass handle in the same language as the frames and steppers.
                var handle = new VisualElement();
                handle.style.position = Position.Absolute;
                float hs = Mathf.Max(12f, U(18f));
                handle.style.width = hs;
                handle.style.height = hs;
                handle.style.backgroundColor = ChipBg;
                Round(handle, hs * 0.5f);
                SetBorderWidth(handle, 2);
                SetBorderColor(handle, accent);
                track.Add(handle);

                var vl = new Label("0.00");
                vl.style.color = Parchment;
                vl.style.fontSize = S(BaseBig) * 0.52f;
                vl.style.unityFontStyleAndWeight = FontStyle.Bold;
                vl.style.width = S(BaseBig) * 1.9f;
                vl.style.unityTextAlign = TextAnchor.MiddleRight;
                vl.style.flexShrink = 0;
                ApplyFont(vl, false);
                head.Add(vl);

                var row = new KnobRow
                {
                    Knob = spec.knob, Min = spec.min, Max = spec.max,
                    Fill = fill, Track = track, Value = vl, Handle = handle, HandleSize = hs
                };
                _knobs.Add(row);

                track.RegisterCallback<PointerDownEvent>(e =>
                {
                    track.CapturePointer(e.pointerId);
                    DragKnob(row, e.localPosition.x);
                    e.StopPropagation();
                });
                track.RegisterCallback<PointerMoveEvent>(e =>
                {
                    if (!track.HasPointerCapture(e.pointerId)) return;
                    DragKnob(row, e.localPosition.x);
                    e.StopPropagation();
                });
                track.RegisterCallback<PointerUpEvent>(e =>
                {
                    if (track.HasPointerCapture(e.pointerId)) track.ReleasePointer(e.pointerId);
                });

                c.Add(line);
            }

            var reset = new Button(() => { _tuner.Reset(_focused); RefreshTuner(); });
            reset.text = "RESET";
            ResetThemeDefaults(reset);
            reset.style.width = Length.Percent(100f);
            reset.style.marginTop = U(4f);
            reset.style.marginLeft = 0f;
            reset.style.marginRight = 0f;
            reset.style.paddingTop = 3;
            reset.style.paddingBottom = 4;
            reset.style.fontSize = S(BaseSection);
            reset.style.letterSpacing = U(1.2f);
            reset.style.color = Gold;
            reset.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.06f);
            SetBorderWidth(reset, 1);
            SetBorderColor(reset, new Color(Gold.r, Gold.g, Gold.b, 0.32f));
            if (displayFont != null)
                reset.style.unityFontDefinition = FontDefinition.FromFont(displayFont);
            c.Add(reset);

            RefreshTuner();
        }

        /// <summary>Turn a pointer x inside the track into a value, and push it to the tuner.</summary>
        private void DragKnob(KnobRow row, float localX)
        {
            float w = row.Track.resolvedStyle.width;
            if (w <= 1f) return;
            float t = Mathf.Clamp01(localX / w);
            _tuner.SetValue(_focused, row.Knob, Mathf.Lerp(row.Min, row.Max, t));
            RefreshTuner();
        }

        /// <summary>Point the sliders at a different segment.</summary>
        private void FocusSegment(int segmentIndex)
        {
            _focused = segmentIndex;

            // Deliberately no badge bounce here. Selecting a row is a quiet act, and a
            // bouncing indicator on every click turns the viewport restless. The panel's
            // highlight carries the message; the camera dip stays reserved for an actual
            // change of outfit, where something really did happen to the glove.
            RefreshTuner();
            RefreshAll();
        }

        /// <summary>Redraw the sliders from whatever the focused material currently holds.</summary>
        private void RefreshTuner()
        {
            if (_tuner == null || _knobs.Count == 0) return;
            var set = assembly != null ? assembly.VariantSet : null;
            if (set == null || _focused < 0 || _focused >= set.segments.Count) return;

            var seg = set.segments[_focused];
            var look = _tuner.LookFor(_focused);
            if (look == null) return;

            if (_tunerTitle != null)
                _tunerTitle.text = seg.DisplayLabel.ToUpperInvariant()
                                 + "  " + seg.VariantLabel(assembly.Selection[_focused])
                                 + (_tuner.IsTouched(_focused) ? "   EDITED" : "");

            foreach (var row in _knobs)
            {
                float v = look.Get(row.Knob);
                float t = Mathf.Clamp01(Mathf.InverseLerp(row.Min, row.Max, v));
                row.Fill.style.width = Length.Percent(t * 100f);
                row.Value.text = v.ToString("0.00");

                float w = row.Track.resolvedStyle.width;
                if (w > 1f) row.Handle.style.left = t * w - row.HandleSize * 0.5f;
            }
        }

        private void BuildAttributeCorner(GloveVariantSet set)
        {
            if (set.attributes == null || set.attributes.Count == 0) return;

            var c = Plate(left: true, top: false);
            c.Add(SectionLabel("ATTRIBUTES"));

            for (int a = 0; a < set.attributes.Count; a++)
            {
                var def = set.attributes[a];
                var accent = ToneFor(a);

                var line = new VisualElement();
                line.style.flexDirection = FlexDirection.Row;
                line.style.alignItems = Align.Center;
                line.style.marginBottom = U(9f);

                var icon = new CustomizerChrome.IconGlyph
                {
                    Shape = CustomizerChrome.GlyphForAttribute(a),
                    Tint = accent
                };
                icon.style.width = S(BaseSection) * 2.2f;
                icon.style.height = S(BaseSection) * 2.2f;
                icon.style.flexShrink = 0;
                icon.style.marginRight = U(9f);
                line.Add(icon);

                var nm = new Label(def.label.ToUpperInvariant());
                nm.style.color = TextMuted;
                nm.style.fontSize = S(BaseSection);
                nm.style.letterSpacing = U(1.2f);
                nm.style.wordSpacing = U(4f);
                nm.style.width = S(BaseSection) * 8.2f;
                nm.style.flexShrink = 0;
                ApplyFont(nm, false);
                line.Add(nm);

                var track = new VisualElement();
                track.style.width = S(BaseSection) * 7f;
                track.style.height = Mathf.Max(2f, U(4f));
                track.style.backgroundColor = Track;
                track.style.marginLeft = U(6f);
                track.style.marginRight = U(10f);
                track.style.flexShrink = 0;
                line.Add(track);

                var fill = new VisualElement();
                fill.style.height = Mathf.Max(2f, U(4f));
                fill.style.width = Length.Percent(0);
                fill.style.backgroundColor = accent;
                track.Add(fill);

                var vl = new Label("00");
                vl.style.color = TextMain;
                vl.style.fontSize = S(BaseBig) * 0.62f;
                vl.style.unityFontStyleAndWeight = FontStyle.Bold;
                vl.style.width = S(BaseBig) * 1.25f;
                vl.style.unityTextAlign = TextAnchor.MiddleRight;
                vl.style.flexShrink = 0;
                ApplyFont(vl, false);
                line.Add(vl);

                c.Add(line);
                _stats.Add(new StatRow { Fill = fill, Value = vl });
            }
        }

        /// <summary>
        /// Bottom-right: every segment's materials in one place, with the whole-glove design
        /// presets stacked beneath them. One column, so the eye runs straight down from the
        /// individual pieces to the complete outfits.
        /// </summary>
        private void BuildOutfitCorner(GloveVariantSet set)
        {
            var c = Plate(left: false, top: true);
            _materialsPlate = c;
            // Runs the full height of the view, stopping just above the control legend.
            // minWidth rather than width: a fixed width lets a wider row (the design tiles)
            // spill past the drawn frame, because the frame traces the element box. With a
            // minimum, the element grows to its content and the border always contains it.
            c.style.bottom = U(46f);
            _bottomAnchored.Add(c);
            c.style.minWidth = U(materialsWidth);
            c.style.alignItems = Align.FlexEnd;
            c.Add(SectionHeading("MODULAR PARTS", "SWAP MATERIAL"));

            // The four rows live in their own container so the walkthrough can light up the
            // swapping controls alone, rather than the whole column including the sliders.
            var rowsBlock = new VisualElement();
            rowsBlock.style.alignItems = Align.FlexEnd;
            c.Add(rowsBlock);
            _materialRowsBlock = rowsBlock;

            for (int s = 0; s < set.segments.Count; s++)
            {
                int segment = s;
                var seg = set.segments[s];
                var accent = AccentFor(s);

                var block = new VisualElement();
                block.style.marginBottom = U(11f);
                block.style.alignItems = Align.FlexEnd;
                rowsBlock.Add(block);

                var head = new VisualElement();
                head.style.flexDirection = FlexDirection.Row;
                head.style.alignItems = Align.Center;
                head.style.marginBottom = U(5f);
                block.Add(head);

                var nm = new Label(seg.DisplayLabel.ToUpperInvariant());
                nm.style.color = TextMuted;
                nm.style.fontSize = S(BaseSection);
                nm.style.letterSpacing = U(1.3f);
                nm.style.wordSpacing = U(4f);
                ApplyFont(nm, false);
                head.Add(nm);

                var val = new Label();
                val.style.color = accent;
                val.style.fontSize = S(BaseHint);
                val.style.marginLeft = U(14f);
                ApplyFont(val, false);
                head.Add(val);

                // ---- carousel: [<] faded | selected | faded [>] ----
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                block.Add(row);

                var left = CarouselChip(() => StepVariant(segment, -1), false,
                                        CustomizerChrome.Glyph.ArrowLeft);
                // Clicking the fitted preview points the material sliders at this segment.
                var centre = CarouselChip(() => FocusSegment(segment), true,
                                          CustomizerChrome.Glyph.Feather);
                var right = CarouselChip(() => StepVariant(segment, 1), false,
                                         CustomizerChrome.Glyph.ArrowRight);
                row.Add(left);
                row.Add(centre);
                row.Add(right);

                _matRows.Add(new MaterialRow { Left = left, Centre = centre, Right = right, Value = val, Name = nm });
            }

            // a spacer, so the designs sit at the foot of the panel rather than
            // floating directly under the last material row
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            c.Add(spacer);

            // ---- the material sliders, at the bottom of this column ----
            // They live here rather than in their own corner because they act on whichever
            // material is focused in the list directly above them. Putting the control next
            // to the thing it controls means the eye never has to cross the screen.
            var rule = new VisualElement();
            rule.style.height = 1;
            rule.style.marginTop = U(6f);
            rule.style.marginBottom = U(12f);
            rule.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.22f);
            c.Add(rule);

            BuildTuner(c, set);
        }

        /// <summary>
        /// Bottom-left: the whole-glove design presets. Each row is a name beside its
        /// picture rather than a caption printed over it, so both stay legible.
        /// </summary>
        private void BuildDesignCorner(GloveVariantSet set)
        {
            int presetCount = 0;
            foreach (var seg in set.segments)
                presetCount = Mathf.Max(presetCount, seg.VariantCount);
            if (presetCount == 0) return;

            var hero = set.FindSegment("bottom") ?? set.segments[0];

            var c = Plate(left: true, top: false);
            _designPlate = c;
            c.style.minWidth = U(identityWidth);
            c.Add(Centred(SectionLabel("PRESETS")));

            for (int i = 0; i < presetCount; i++)
            {
                int preset = i;
                var tile = new Button(() => ApplyPreset(preset));
                tile.text = string.Empty;
                ResetThemeDefaults(tile);
                tile.style.flexDirection = FlexDirection.Row;
                tile.style.alignItems = Align.Center;
                tile.style.width = Length.Percent(100f);
                tile.style.marginBottom = U(7f);
                tile.style.paddingLeft = U(8f);
                tile.style.paddingRight = U(6f);
                tile.style.paddingTop = U(5f);
                tile.style.paddingBottom = U(5f);
                tile.style.backgroundColor = new Color(1f, 0.94f, 0.78f, 0.05f);
                SetBorderWidth(tile, 1);

                var cap = new Label("DESIGN " + (i + 1));
                cap.style.fontSize = S(BaseSection) * 1.12f;
                cap.style.letterSpacing = U(1.3f);
                cap.style.wordSpacing = U(4f);
                cap.style.color = Parchment;
                cap.style.unityFontStyleAndWeight = FontStyle.Bold;
                cap.style.flexGrow = 1f;
                cap.style.unityTextAlign = TextAnchor.MiddleLeft;
                ApplyFont(cap, true);
                tile.Add(cap);

                // A whole-outfit image if one has been supplied, otherwise fall back to the
                // hero part's preview. The fallback only ever showed one piece of the glove,
                // which is a poor stand-in for a complete design.
                var thumb = set.DesignThumbnail(preset)
                            ?? (preset < hero.VariantCount ? PreviewFor(hero, preset) : null);

                // One shape for all three, not one per image. Sizing each box to its own
                // picture made the three rows different heights, which reads as a mistake.
                // Now that previews fill and crop, the box can hold its shape and let each
                // image adapt to it instead.
                var art = new VisualElement();
                float artW = U(presetWidth * 1.85f);
                art.style.width = artW;
                art.style.height = artW / 1.75f;
                art.style.flexShrink = 0;
                if (thumb != null)
                {
                    art.style.backgroundImage = new StyleBackground(thumb);
                    Fit(art);
                }
                else art.style.backgroundColor = ChipBg;
                SetBorderWidth(art, 1);
                SetBorderColor(art, new Color(ChipBorder.r, ChipBorder.g, ChipBorder.b, 0.5f));
                tile.Add(art);

                c.Add(tile);
                _presetTiles.Add(tile);
            }
        }


        /// <summary>
        /// One slot in a material carousel. The centre slot carries the fitted material at
        /// full strength; the flanking slots preview one step either way, dissolved back and
        /// carrying the stepper chevron on top of themselves. Putting the arrow over the
        /// faded preview instead of beside it keeps the panel narrow, which matters when it
        /// is competing with the 3D view for width.
        /// </summary>
        private VisualElement CarouselChip(System.Action onClick, bool centre,
                                           CustomizerChrome.Glyph arrow)
        {
            VisualElement chip;
            if (onClick != null)
            {
                var b = new Button(onClick) { text = string.Empty };
                ResetThemeDefaults(b);
                chip = b;
            }
            else
            {
                chip = new VisualElement();
            }

            // Width and height are computed separately on purpose. They used to share a
            // formula, so making a preview wider also made the whole materials list taller
            // and pushed the design section off the bottom of the panel. The height terms
            // below are the original proportions, frozen.
            chip.style.width = U(swatchWidth * (centre ? swatchCentreWide : swatchSideWide));
            chip.style.height = U(swatchWidth * (centre ? 1.28f * 0.74f : 0.80f * 0.90f));
            chip.style.marginLeft = centre ? U(3f) : 0f;
            chip.style.marginRight = centre ? U(3f) : 0f;
            chip.style.flexShrink = 0;
            chip.style.backgroundColor = ChipBg;
            chip.style.overflow = Overflow.Hidden;
            SetBorderWidth(chip, centre ? 2 : 1);
            SetBorderColor(chip, centre ? Gold : new Color(ChipBorder.r, ChipBorder.g, ChipBorder.b, 0.30f));

            if (centre) return chip;

            // Fade the PREVIEW, not the element. style.opacity cascades to children, so
            // dimming the chip also dimmed the chevron sitting on it — which is why the
            // arrows read so faintly. Tinting the background image leaves the glyph alone.
            chip.style.unityBackgroundImageTintColor = new Color(1f, 1f, 1f, 0.11f);

            // These were deliberately understated, on the reasoning that a stepper is a
            // hint rather than a headline. That went too far: sitting on top of a dimmed
            // preview they read as disabled, and a control nobody notices might as well not
            // be there. Brighter than the frames, and large enough to aim at.
            var glyph = new CustomizerChrome.IconGlyph
            {
                Shape = arrow,
                Weight = 1.15f,
                Tint = new Color(1f, 0.91f, 0.58f, 1f)
            };
            glyph.style.position = Position.Absolute;
            glyph.style.alignSelf = Align.Center;
            // Centred: top has to be half of whatever is left over, or a bigger glyph drifts
            // down out of its chip.
            glyph.style.top = Length.Percent(21f);
            glyph.style.width = Length.Percent(58f);
            glyph.style.height = Length.Percent(58f);
            chip.style.alignItems = Align.Center;
            chip.Add(glyph);

            return chip;
        }

        /// <summary>
        /// Channel isolation, centred above the control legend. Sitting on the mid-line
        /// rather than in a corner is deliberate: these change what the whole viewport is
        /// showing, so the control belongs under the thing it changes, not filed away with
        /// the panels that edit one part.
        /// </summary>
        /// <summary>
        /// A first-run walkthrough. Six stops, each pointing at one panel.
        ///
        /// The interface is dense -- four panels, a carousel, five sliders and two export
        /// paths -- and a portfolio piece is usually opened by someone with no context and
        /// little patience. Half a minute of guidance is the difference between understanding
        /// what this does and closing the tab.
        /// </summary>
        private void BuildTour()
        {
            _tour = new CustomizerTour(_root, S, displayFont);

            _tour.SetWelcome("RECAST CUSTOMIZER",
                "A material review tool for modular game assets. Swap the material on each "
                + "part, adjust how it looks, and export the result for an artist to apply "
                + "back in the engine.");

            // Anchored to the control legend, because the subject is the controls. A step
            // whose title says "view control" should point at where the controls are listed.
            _tour.AddStep("3D VIEW CONTROL",
                "Drag to turn the asset. Hold the middle mouse button to slide the view. "
                + "Scroll to zoom. These are listed here whenever you need them.",
                () => _hintBar);

            // Only the swapping rows are lit, not the whole column, and every part marker in
            // the view is ringed so the link between the two is visible rather than described.
            _tour.AddStep("MODULAR PARTS",
                "One row per part. The arrows step through the materials available to it. "
                + "Each row matches a ringed marker on the asset -- click either one to "
                + "select that part for editing.",
                () => _materialRowsBlock,
                () =>
                {
                    var list = new List<VisualElement>();
                    foreach (var c in _callouts)
                        if (c.Badge != null && c.Badge.resolvedStyle.display == DisplayStyle.Flex)
                            list.Add(c.Badge);
                    return list;
                },
                () => { if (Orbit != null) Orbit.ZoomTo(1.45f); });

            _tour.AddStep("LOOK DEV",
                "Five controls for the part you have selected: hue, saturation, brightness, "
                + "smoothness and normal strength. Reset returns it to how the artist made it.",
                () => _tunerBlockFull,
                null,
                () => { if (Orbit != null) Orbit.ZoomTo(1f); });

            _tour.AddStep("PRESETS",
                "Three ready-made designs. One click dresses all four parts at once, "
                + "so you always have a finished starting point to work back from.",
                () => _designPlate);

            _tour.AddStep("ASSET DATA",
                "Triangle count, texture size, and how many designs are possible. "
                + "The memory figure is live: it changes as you swap materials, because "
                + "not every combination costs the same.",
                () => _identityPlate);

            _tour.AddStep("TAKE IT WITH YOU",
                "Three ways to leave with what you decided." + "\n\n"
                + "PNG is a picture of the view, for showing someone." + "\n\n"
                + "JSON is the one that does the work: an artist drops it into the engine "
                + "and your changes are applied to the real materials." + "\n\n"
                + "TXT is the same values in plain words, for an email or a ticket.",
                () => _exportChip);

            // No longer gated on whether this machine has seen it before. The tour still
            // records that it was seen, so first-run-only remains one line away, but for a
            // portfolio piece the visitor is almost always new and the reviewer who opens it
            // twice can press Skip.
            if (Application.isPlaying && alwaysShowGuide)
                _tour.Start();
        }

        private void BuildViewModes()
        {
            if (!showViewModes || _views == null) return;

            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = 0; bar.style.right = 0;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.justifyContent = Justify.Center;
            bar.style.alignItems = Align.Center;
            _root.Add(bar);
            _viewBar = bar;

            var modes = new (ViewModeController.Mode mode, string label)[]
            {
                (ViewModeController.Mode.Lit,        "LIT"),
                (ViewModeController.Mode.Albedo,     "ALBEDO"),
                (ViewModeController.Mode.Normal,     "NORMAL"),
                (ViewModeController.Mode.Smoothness, "SMOOTH"),
                (ViewModeController.Mode.UV,         "UV"),
                (ViewModeController.Mode.Wireframe,  "WIRE")
            };

            foreach (var m in modes)
            {
                var mode = m.mode;
                var chip = new Button(() => { _views.Set(mode); RefreshViewChips(); });
                chip.text = m.label;
                ResetThemeDefaults(chip);
                chip.style.marginLeft = U(5f);
                chip.style.marginRight = U(5f);
                chip.style.paddingLeft = U(13f);
                chip.style.paddingRight = U(13f);
                chip.style.paddingTop = U(4f);
                chip.style.paddingBottom = U(5f);
                chip.style.fontSize = S(BaseHint);
                chip.style.letterSpacing = U(1.6f);
                SetBorderWidth(chip, 1);
                if (displayFont != null)
                    chip.style.unityFontDefinition = FontDefinition.FromFont(displayFont);
                bar.Add(chip);
                _viewChips.Add(chip);
            }

            // The legend measures itself after layout, so this bar has to wait for the same
            // moment before it can know where to sit above it.
            bar.RegisterCallback<GeometryChangedEvent>(_ => PlaceViewBar(bar));
            RefreshViewChips();
        }

        private void PlaceViewBar(VisualElement bar)
        {
            float legend = _hintBar != null ? _hintBar.resolvedStyle.height : U(46f);
            bar.style.bottom = legend + U(18f);
        }

        private void RefreshViewChips()
        {
            if (_views == null) return;
            for (int i = 0; i < _viewChips.Count; i++)
            {
                bool on = (int)_views.Current == i;
                _viewChips[i].style.color = on ? Gold : TextMuted;
                _viewChips[i].style.backgroundColor = on
                    ? new Color(Gold.r, Gold.g, Gold.b, 0.14f)
                    : new Color(0.02f, 0.02f, 0.016f, 0.55f);
                SetBorderColor(_viewChips[i], new Color(Gold.r, Gold.g, Gold.b, on ? 0.62f : 0.22f));
            }
        }

        private void BuildHints()
        {
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = 0; bar.style.right = 0; bar.style.bottom = 0;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.justifyContent = Justify.Center;
            bar.style.alignItems = Align.Center;
            bar.style.paddingTop = 7;
            bar.style.paddingBottom = 9;
            bar.style.backgroundColor = new Color(0.02f, 0.02f, 0.016f, 0.72f);
            _root.Add(bar);

            // The guide leads, because it is the one control a first-time visitor needs
            // before any of the others make sense. A key that is only mentioned is a key
            // nobody presses, so the legend entry is the button as well as the label.
            var guideHint = Hint("G", "GUIDE");
            guideHint.pickingMode = PickingMode.Position;
            guideHint.RegisterCallback<ClickEvent>(_ =>
            {
                if (_tour != null && !_tour.IsRunning) _tour.Start();
            });
            bar.Add(guideHint);

            bar.Add(Hint("DRAG", "ORBIT"));
            bar.Add(Hint("MMB", "PAN"));
            bar.Add(Hint("SCROLL", "ZOOM"));
            bar.Add(Hint("R", "RANDOMIZE"));

            // The three exports read as one set, because they are one: the same session
            // saved as a picture, as data, and as words. Capture used to sit among the
            // keyboard hints, which made it look like a reminder rather than a control.
            // They are named for the file each one produces, so nobody has to guess.
            // Grouped, so the walkthrough can light all three at once. The final step is
            // about the choice between them, and highlighting one button while describing
            // three would point the reader at the wrong thing.
            var exportGroup = new VisualElement();
            exportGroup.style.flexDirection = FlexDirection.Row;
            exportGroup.style.alignItems = Align.Center;
            exportGroup.style.marginLeft = U(16f);
            bar.Add(exportGroup);
            _exportChip = exportGroup;

            var capture = LegendButton("CAPTURE PNG", () => StartCoroutine(CaptureScreenshot()));
            capture.style.marginLeft = 0f;
            exportGroup.Add(capture);

            exportGroup.Add(LegendButton("EXPORT JSON", ExportLookFile));
            exportGroup.Add(LegendButton("EXPORT TXT", ExportLookNotes));

            // The legend's height comes from its own content and padding, so a hardcoded
            // clearance goes stale the moment the font or scale changes -- which is exactly
            // how the panels ended up sitting underneath it. Measure the bar once it has
            // been laid out, and lift every bottom-anchored plate above whatever it turned
            // out to be.
            _hintBar = bar;
            bar.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float clear = bar.resolvedStyle.height + U(12f);
                foreach (var el in _bottomAnchored)
                {
                    // the view-mode row, when shown, sits its own distance above the legend
                    if (el == _viewBar) continue;
                    el.style.bottom = clear;
                }
                if (_viewBar != null) PlaceViewBar(_viewBar);
            });
        }

        /// <summary>
        /// An action button for the control legend. One builder for all of them: two buttons
        /// styled separately drift apart the moment either is touched, which is exactly what
        /// happened to these.
        /// </summary>
        private Button LegendButton(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = Spaced(text) };
            ResetThemeDefaults(b);
            b.style.marginLeft = U(16f);   // the same gap the hints put between themselves
            b.style.marginRight = 0f;
            b.style.paddingLeft = 15;
            b.style.paddingRight = 15;
            b.style.paddingTop = 4;
            b.style.paddingBottom = 5;
            b.style.fontSize = S(BaseHint);
            b.style.letterSpacing = 2f;
            b.style.color = Gold;
            b.style.backgroundColor = new Color(Gold.r, Gold.g, Gold.b, 0.10f);
            SetBorderWidth(b, 1);
            SetBorderColor(b, new Color(Gold.r, Gold.g, Gold.b, 0.45f));
            if (displayFont != null)
                b.style.unityFontDefinition = FontDefinition.FromFont(displayFont);
            return b;
        }

        private VisualElement Hint(string key, string what)
        {
            var wrap = new VisualElement();
            wrap.style.flexDirection = FlexDirection.Row;
            wrap.style.alignItems = Align.Center;
            wrap.style.marginRight = U(16f);

            var chip = new Label(key);
            chip.style.color = TextMain;
            chip.style.fontSize = S(BaseHint);
            chip.style.paddingLeft = 5;
            chip.style.paddingRight = 5;
            chip.style.paddingTop = 1;
            chip.style.paddingBottom = 2;
            chip.style.backgroundColor = new Color(1f, 0.94f, 0.78f, 0.10f);
            SetBorderWidth(chip, 1);
            SetBorderColor(chip, new Color(1f, 0.94f, 0.78f, 0.20f));
            ApplyFont(chip, false);
            wrap.Add(chip);

            var text = new Label(what);
            text.style.color = TextMuted;
            text.style.fontSize = S(BaseHint);
            text.style.letterSpacing = U(1.1f);
            text.style.marginLeft = U(6f);
            ApplyFont(text, false);
            wrap.Add(text);
            return wrap;
        }

        // ---------------------------------------------------------------- overlays

        private void BuildVignette()
        {
            if (vignetteStrength <= 0.001f) return;
            var v = new VisualElement();
            v.style.position = Position.Absolute;
            v.style.left = 0; v.style.top = 0; v.style.right = 0; v.style.bottom = 0;
            v.pickingMode = PickingMode.Ignore;
            v.style.backgroundImage = new StyleBackground(BuildVignetteTexture());
            v.style.unityBackgroundImageTintColor = new Color(1f, 1f, 1f, vignetteStrength);
            _root.Add(v);
        }

        private static Texture2D BuildVignetteTexture(int size = 128)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            var px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Sqrt(2f);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 1f, d));
                    px[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        private void BuildLineLayer()
        {
            _lineLayer = new VisualElement();
            _lineLayer.style.position = Position.Absolute;
            _lineLayer.style.left = 0; _lineLayer.style.top = 0;
            _lineLayer.style.right = 0; _lineLayer.style.bottom = 0;
            _lineLayer.pickingMode = PickingMode.Ignore;
            _lineLayer.generateVisualContent += DrawLeaderLines;
            _root.Add(_lineLayer);
        }

        /// <summary>
        /// Leader lines drawn with an elbow rather than a straight run — a short horizontal
        /// stub off the badge, then a diagonal to the mesh. Reads as a technical callout.
        /// After a change they redraw from the badge outward.
        /// </summary>
        private void DrawLeaderLines(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            p.lineWidth = 1.2f;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            for (int i = 0; i < _callouts.Count; i++)
            {
                var c = _callouts[i];
                if (!c.Visible) continue;
                var accent = AccentFor(i);

                var a = c.BadgeCentre;
                var b = c.AnchorPoint;
                float stub = Mathf.Min(24f, Vector2.Distance(a, b) * 0.32f);
                var elbow = new Vector2(a.x + (b.x >= a.x ? stub : -stub), a.y);

                // reveal: 0 draws nothing, 1 draws the whole run
                float reveal = Mathf.Clamp01(c.Reveal);
                var mid = Vector2.Lerp(a, elbow, Mathf.Clamp01(reveal * 3f));
                var end = Vector2.Lerp(elbow, b, Mathf.Clamp01((reveal - 0.33f) * 1.5f));

                p.strokeColor = new Color(accent.r, accent.g, accent.b, 0.75f);
                p.BeginPath();
                p.MoveTo(a);
                p.LineTo(mid);
                if (reveal > 0.33f) p.LineTo(end);
                p.Stroke();

                if (reveal > 0.98f)
                {
                    p.fillColor = accent;
                    p.BeginPath();
                    p.Arc(b, 3.2f, 0f, 360f);
                    p.Fill();
                }
            }
        }

        // ---------------------------------------------------------------- callouts

        private void BuildCallouts(GloveVariantSet set)
        {
            for (int i = 0; i < set.segments.Count; i++)
            {
                var seg = set.segments[i];
                var accent = AccentFor(i);

                // Select, then act. The first click on a badge points the sliders at that
                // part; once it is already selected, clicking again cycles its material.
                // Doing both on one click would change the material out from under someone
                // who was only trying to pick which part to tune.
                int segIndex = i;
                var badge = new Button(() =>
                {
                    if (_focused != segIndex) FocusSegment(segIndex);
                    else assembly.CycleVariant(seg.id);
                });
                badge.text = string.Empty;
                ResetThemeDefaults(badge);
                badge.style.position = Position.Absolute;
                badge.style.width = U(badgeSize);
                badge.style.height = U(badgeSize);
                badge.style.backgroundColor = ChipBg;
                Round(badge, U(badgeSize) * 0.5f);
                SetBorderWidth(badge, 2);
                SetBorderColor(badge, accent);
                badge.style.display = DisplayStyle.None;
                _root.Add(badge);

                // the arc ring, a partial stroke sitting outside the badge
                var ring = new VisualElement();
                ring.pickingMode = PickingMode.Ignore;
                ring.style.position = Position.Absolute;
                ring.style.left = -6; ring.style.top = -6;
                ring.style.right = -6; ring.style.bottom = -6;
                Round(ring, (badgeSize + 12f) * 0.5f);
                ring.style.borderTopWidth = 1;
                ring.style.borderRightWidth = 1;
                ring.style.borderTopColor = new Color(accent.r, accent.g, accent.b, 0.55f);
                ring.style.borderRightColor = new Color(accent.r, accent.g, accent.b, 0.55f);
                badge.Add(ring);

                var box = new VisualElement();
                box.style.position = Position.Absolute;
                box.style.alignItems = Align.Center;
                box.style.paddingLeft = 10; box.style.paddingRight = 10;
                box.style.paddingTop = 5; box.style.paddingBottom = 6;
                box.style.backgroundColor = new Color(0.035f, 0.032f, 0.026f, 0.90f);
                SetBorderWidth(box, 1);
                SetBorderColor(box, new Color(accent.r, accent.g, accent.b, 0.48f));
                box.style.display = DisplayStyle.None;
                _root.Add(box);

                var cat = new Label(seg.DisplayLabel.ToUpperInvariant());
                cat.style.color = TextMain;
                cat.style.fontSize = S(BaseSection);
                cat.style.letterSpacing = U(1.4f);
                cat.style.wordSpacing = U(4f);
                ApplyFont(cat, false);
                box.Add(cat);

                var val = new Label();
                val.style.color = accent;
                val.style.fontSize = S(BaseHint);
                val.style.marginTop = 2;
                ApplyFont(val, false);
                box.Add(val);

                _callouts.Add(new Callout
                {
                    Badge = badge, LabelBox = box, Value = val,
                    Anchor = FindSegmentTransform(seg.childRenderer)
                });
            }
        }

        private Transform FindSegmentTransform(string childName)
        {
            if (assembly == null || string.IsNullOrEmpty(childName)) return null;
            foreach (var r in assembly.GetComponentsInChildren<Renderer>(true))
                if (r.gameObject.name == childName) return r.transform;
            return null;
        }

        /// <summary>Badge pop and line redraw, both eased over <see cref="popTime"/>.</summary>
        private void AnimatePops()
        {
            float dt = Time.unscaledDeltaTime;
            float step = popTime <= 0f ? 1f : dt / popTime;

            foreach (var c in _callouts)
            {
                if (c.Reveal < 1f) c.Reveal = Mathf.Min(1f, c.Reveal + step);
                if (c.Pop <= 0f) continue;
                c.Pop = Mathf.Max(0f, c.Pop - step);

                // a single overshoot on the way back to rest
                float bump = Mathf.Sin(c.Pop * Mathf.PI) * 0.18f;
                float size = badgeSize * (1f + bump);
                c.Badge.style.width = size;
                c.Badge.style.height = size;
                Round(c.Badge, size * 0.5f);
            }
        }

        private void UpdateCallouts()
        {
            if (_root == null || _callouts.Count == 0) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var rect = _root.contentRect;
            if (float.IsNaN(rect.width) || rect.width <= 0f) return;
            if (!TryGetScreenSilhouette(out var modelCentre, out float modelRadius)) return;

            float half = badgeSize * 0.5f;
            float orbit = modelRadius + calloutClearance + half;

            for (int i = 0; i < _callouts.Count; i++)
            {
                var c = _callouts[i];
                if (c.Anchor == null) { HideCallout(c); continue; }

                var r = c.Anchor.GetComponent<Renderer>();
                var world = r != null ? r.bounds.center : c.Anchor.position;

                if (Vector3.Dot(_cam.transform.forward, world - _cam.transform.position) <= 0f)
                { HideCallout(c); continue; }

                var segCentre = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, world, _cam);
                float segRadius = 0f;
                if (r != null && TryProjectBounds(r.bounds, out var pc, out var pr))
                { segCentre = pc; segRadius = pr; }
                if (float.IsNaN(segCentre.x) || float.IsNaN(segCentre.y)) { HideCallout(c); continue; }

                Vector2 dir;
                if (calloutAngles != null && i < calloutAngles.Length)
                {
                    float rad = calloutAngles[i] * Mathf.Deg2Rad;
                    dir = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
                }
                else
                {
                    dir = segCentre - modelCentre;
                    if (dir.sqrMagnitude < 4f) dir = new Vector2(-1f, -1f);
                    dir.Normalize();
                }

                // clamped against the SEGMENT's outline, so the dot lands on the part it names
                float reach = segRadius * Mathf.Min(calloutAnchorBias, 0.45f);
                c.AnchorPoint = segCentre + dir * reach;
                c.BadgeCentre = modelCentre + dir * orbit;
                c.Visible = true;
            }

            for (int pass = 0; pass < 6; pass++)
            {
                bool moved = false;
                for (int a = 0; a < _callouts.Count; a++)
                {
                    if (!_callouts[a].Visible) continue;
                    for (int b = a + 1; b < _callouts.Count; b++)
                    {
                        if (!_callouts[b].Visible) continue;
                        var d = _callouts[b].BadgeCentre - _callouts[a].BadgeCentre;
                        float dist = d.magnitude;
                        if (dist >= calloutSpacing || dist < 0.001f) continue;
                        var push = d / dist * ((calloutSpacing - dist) * 0.5f);
                        _callouts[a].BadgeCentre -= push;
                        _callouts[b].BadgeCentre += push;
                        moved = true;
                    }
                }
                for (int i = 0; i < _callouts.Count; i++)
                {
                    if (!_callouts[i].Visible) continue;
                    var d = _callouts[i].BadgeCentre - modelCentre;
                    if (d.sqrMagnitude < 0.001f) continue;
                    _callouts[i].BadgeCentre = modelCentre + d.normalized * orbit;
                }
                if (!moved) break;
            }

            for (int i = 0; i < _callouts.Count; i++)
            {
                var c = _callouts[i];
                if (!c.Visible) continue;

                var p = c.BadgeCentre;
                p.x = Mathf.Clamp(p.x, calloutMargin + half, rect.width - calloutMargin - half);
                p.y = Mathf.Clamp(p.y, calloutMargin + half + 56f, rect.height - half - 120f);
                c.BadgeCentre = p;

                float size = c.Badge.resolvedStyle.width;
                if (size < 1f) size = badgeSize;

                c.Badge.style.display = DisplayStyle.Flex;
                c.Badge.style.left = p.x - size * 0.5f;
                c.Badge.style.top = p.y - size * 0.5f;

                c.LabelBox.style.display = DisplayStyle.Flex;
                float w = Mathf.Max(c.LabelBox.resolvedStyle.width, 1f);
                c.LabelBox.style.left = p.x - w * 0.5f;
                c.LabelBox.style.top = p.y + size * 0.5f + 7f;
            }

            _lineLayer.MarkDirtyRepaint();
        }

        private bool TryGetScreenSilhouette(out Vector2 centre, out float radius)
        {
            centre = Vector2.zero; radius = 0f;
            if (_segmentRenderers == null || _segmentRenderers.Length == 0)
            {
                _segmentRenderers = assembly != null
                    ? assembly.GetComponentsInChildren<Renderer>(true)
                    : System.Array.Empty<Renderer>();
                if (_segmentRenderers.Length == 0) return false;
            }
            var b = _segmentRenderers[0].bounds;
            for (int i = 1; i < _segmentRenderers.Length; i++) b.Encapsulate(_segmentRenderers[i].bounds);
            if (!TryProjectBounds(b, out centre, out radius)) return false;
            radius = Mathf.Max(radius, U(badgeSize));
            return true;
        }

        private bool TryProjectBounds(Bounds bounds, out Vector2 centre, out float radius)
        {
            radius = 0f;
            centre = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, bounds.center, _cam);
            if (float.IsNaN(centre.x) || float.IsNaN(centre.y)) return false;

            var e = bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                if (Vector3.Dot(_cam.transform.forward, corner - _cam.transform.position) <= 0f) continue;
                var p = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, corner, _cam);
                if (float.IsNaN(p.x) || float.IsNaN(p.y)) continue;
                radius = Mathf.Max(radius, Vector2.Distance(p, centre));
            }
            return true;
        }

        private static void HideCallout(Callout c)
        {
            c.Visible = false;
            c.Badge.style.display = DisplayStyle.None;
            c.LabelBox.style.display = DisplayStyle.None;
        }

        // ---------------------------------------------------------------- state

        /// <summary>
        /// A whole-outfit change: every segment moves, so every callout answers and the
        /// camera dips once for the lot.
        /// </summary>
        private void ApplyPreset(int preset)
        {
            var set = assembly.VariantSet;
            _applyingPreset = true;
            for (int s = 0; s < set.segments.Count; s++)
            {
                int count = set.segments[s].VariantCount;
                if (count == 0) continue;
                assembly.SetVariantAt(s, Mathf.Clamp(preset, 0, count - 1));
            }
            _applyingPreset = false;

            foreach (var c in _callouts) { c.Pop = 1f; c.Reveal = 0f; }
            if (Orbit != null) Orbit.Breathe();

            // The glove is rigid, but the tassel hangs, so it should answer a change of
            // outfit a beat after the camera does. Motion that trails the main event is what
            // makes a static prop read as a physical object.
            foreach (var sway in assembly.GetComponentsInChildren<SecondaryMotion>(true))
                sway.Nudge();

            RefreshAll();
        }

        /// <summary>Steps one segment through its variants, wrapping at either end.</summary>
        private void StepVariant(int segment, int delta)
        {
            var set = assembly.VariantSet;
            if (segment < 0 || segment >= set.segments.Count) return;
            int count = set.segments[segment].VariantCount;
            if (count == 0) return;
            int next = ((assembly.Selection[segment] + delta) % count + count) % count;
            assembly.SetVariantAt(segment, next);
        }

        private int ActivePreset()
        {
            var set = assembly.VariantSet;
            if (set.segments.Count == 0) return -1;
            int first = assembly.Selection[0];
            for (int s = 1; s < set.segments.Count; s++)
                if (assembly.Selection[s] != first) return -1;
            return first;
        }

        private void RefreshAll() => RefreshAll(false);

        private void RefreshAll(bool instant)
        {
            if (assembly == null || assembly.VariantSet == null) return;
            var set = assembly.VariantSet;

            UpdateCostRow();

            for (int s = 0; s < set.segments.Count; s++)
            {
                var seg = set.segments[s];
                int sel = assembly.Selection[s];
                var accent = AccentFor(s);

                if (s < _callouts.Count)
                {
                    var c = _callouts[s];
                    c.Value.text = seg.VariantLabel(sel);
                    var thumb = PreviewFor(seg, sel);
                    if (thumb != null)
                    {
                        c.Badge.style.backgroundImage = new StyleBackground(thumb);
                        Fit(c.Badge);
                    }

                    // The same selection, said in the viewport. Someone working on the model
                    // should not have to look at the panel to know which part is armed.
                    bool badgeFocused = s == _focused;
                    SetBorderWidth(c.Badge, badgeFocused ? 3 : 2);
                    SetBorderColor(c.Badge, badgeFocused
                        ? accent
                        : new Color(accent.r, accent.g, accent.b, 0.55f));
                    // Only a light knock-back here. The badges are small and already
                    // competing with the model behind them, so the border weight does the
                    // talking; pushing their colour as far as the panel rows read as broken
                    // rather than deselected.
                    c.Badge.style.unityBackgroundImageTintColor = badgeFocused
                        ? Color.white
                        : new Color(0.86f, 0.87f, 0.89f, 1f);
                }

                if (s < _matRows.Count)
                {
                    var mr = _matRows[s];
                    mr.Value.text = seg.VariantLabel(sel);

                    int count = Mathf.Max(1, seg.VariantCount);
                    int prev = ((sel - 1) % count + count) % count;
                    int next = (sel + 1) % count;

                    SetChipImage(mr.Left, seg, prev);
                    SetChipImage(mr.Centre, seg, sel);
                    SetChipImage(mr.Right, seg, next);

                    // Which segment the sliders are acting on has to be unmissable, so it
                    // is said three ways: the focused row keeps full strength while the rest
                    // dim, its frame takes the segment accent at full brightness, and its
                    // name brightens. The 3D callout is popped separately on focus.
                    // Enough separation to be unmistakable, not so much that the unpicked
                    // rows stop being usable previews. The earlier pass pushed them to a
                    // third strength and well toward grey, which read as switched off rather
                    // than merely not selected -- an artist still needs to judge those.
                    bool focused = s == _focused;
                    mr.Centre.style.opacity = focused ? 1f : 0.68f;
                    mr.Centre.style.unityBackgroundImageTintColor = focused
                        ? Color.white
                        : new Color(0.88f, 0.89f, 0.92f, 1f);
                    SetBorderWidth(mr.Centre, focused ? 3 : 1);
                    SetBorderColor(mr.Centre, focused
                        ? accent
                        : new Color(accent.r, accent.g, accent.b, 0.34f));
                    mr.Centre.style.backgroundColor = focused
                        ? new Color(accent.r, accent.g, accent.b, 0.10f)
                        : ChipBg;

                    if (mr.Name != null)
                    {
                        mr.Name.style.color = focused ? accent : TextMuted;
                        mr.Name.style.unityFontStyleAndWeight =
                            focused ? FontStyle.Bold : FontStyle.Normal;
                    }
                    if (mr.Value != null)
                        mr.Value.style.color = focused ? Parchment : TextMuted;

                    SetBorderColor(mr.Centre, accent);
                    mr.Centre.style.translate = new Translate(0, -3);
                }
            }

            int active = ActivePreset();
            for (int i = 0; i < _presetTiles.Count; i++)
            {
                bool on = i == active;
                _presetTiles[i].style.color = on ? TextMain : TextMuted;
                SetBorderWidth(_presetTiles[i], on ? 2 : 1);
                SetBorderColor(_presetTiles[i], on ? Gold : new Color(1f, 0.94f, 0.78f, 0.14f));
                _presetTiles[i].style.opacity = on ? 1f : 0.58f;
                _presetTiles[i].style.translate = new Translate(0, on ? -3 : 0);
            }

            for (int a = 0; a < _stats.Count && a < set.attributes.Count; a++)
            {
                _stats[a].Target = set.TotalAttribute(a, assembly.Selection);
                if (instant) _stats[a].Shown = _stats[a].Target;
            }
            if (instant) AnimateStats(force: true);
        }

        private void AnimateStats(bool force = false)
        {
            if (_stats.Count == 0) return;
            float k = statSettleTime <= 0f
                ? 1f
                : 1f - Mathf.Exp(-Time.unscaledDeltaTime / (statSettleTime * 0.35f));

            foreach (var row in _stats)
            {
                if (force) row.Shown = row.Target;
                else if (Mathf.Abs(row.Target - row.Shown) < 0.0005f) row.Shown = row.Target;
                else row.Shown = Mathf.Lerp(row.Shown, row.Target, Mathf.Clamp01(k));

                row.Fill.style.width = Length.Percent(Mathf.Clamp01(row.Shown) * 100f);
                row.Value.text = Mathf.RoundToInt(row.Shown * 99f).ToString("00");
            }
        }

        /// <summary>
        /// Fill the box with an image, cropping whatever does not fit, without distorting it.
        ///
        /// Two wrong answers were tried before this one. Stretching to fill is the default and
        /// squashes the shape being reviewed, which is the one thing a preview must never do.
        /// Fitting inside preserves the shape but leaves dead bars around it, and a row of
        /// previews framed in black reads as broken rather than tidy. Filling and cropping
        /// keeps the proportions honest and the panel solid; the edges of a preview carry
        /// almost no information anyway.
        /// </summary>
        private static void Fit(VisualElement el)
        {
            el.style.backgroundSize = new StyleBackgroundSize(
                new BackgroundSize(BackgroundSizeType.Cover));
            el.style.backgroundPositionX =
                new BackgroundPosition(BackgroundPositionKeyword.Center);
            el.style.backgroundPositionY =
                new BackgroundPosition(BackgroundPositionKeyword.Center);
            el.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            // Cover deliberately overflows the box, so the box has to clip it.
            el.style.overflow = Overflow.Hidden;
        }

        private static void SetChipImage(VisualElement chip, GloveVariantSet.Segment seg, int variant)
        {
            var thumb = PreviewFor(seg, variant);
            if (thumb != null)
            {
                chip.style.backgroundImage = new StyleBackground(thumb);
                Fit(chip);
            }
            else chip.style.backgroundImage = null;
        }

        private static Texture2D PreviewFor(GloveVariantSet.Segment seg, int variant)
        {
            var baked = seg.VariantThumbnail(variant);
            if (baked != null) return baked;
            var mat = variant >= 0 && variant < seg.VariantCount ? seg.variants[variant] : null;
            return mat != null ? mat.mainTexture as Texture2D : null;
        }

        // ---------------------------------------------------------------- screenshot

        /// <summary>
        /// Write the tuned values to a file the Unity side can read back. A screenshot ends
        /// the conversation with a picture somebody has to interpret; this ends it with
        /// numbers the artist can apply in one menu click.
        /// </summary>
        private void ExportLookFile()
        {
            if (_tuner == null) return;

            if (_tuner.TouchedCount() == 0)
            {
                Debug.Log("[GloveCustomizerUI] Nothing to export -- no material has been " +
                          "adjusted yet. Drag a slider first.");
                return;
            }

            var json = _tuner.ToJson();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var fileName = assembly.VariantSet.assetName + "_look_"
                         + assembly.GetCombinationCode() + ".json";

#if UNITY_WEBGL && !UNITY_EDITOR
            RecastDownloadFile(fileName, bytes, bytes.Length);
            Debug.Log("[GloveCustomizerUI] Look file sent to browser: " + fileName);
#else
            var dir = Path.Combine(Directory.GetCurrentDirectory(), screenshotFolder);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, fileName);
            File.WriteAllText(file, json);
            Debug.Log("[GloveCustomizerUI] Look file saved: " + file
                      + "  |  Apply it with Recast Customizer > Apply Look File.");
#endif
        }

        /// <summary>
        /// The same decisions as the look file, written as plain text.
        ///
        /// The JSON is for the tool; this is for the person. A note saying "top 02,
        /// smoothness 0.54, brightness up" can be pasted into an email, a review thread or a
        /// ticket and read by a producer who will never open the engine. Both exports carry
        /// the same information and are deliberately separate, because a format that tries to
        /// be machine-readable and human-readable at once usually fails at both.
        /// </summary>
        private void ExportLookNotes()
        {
            if (_tuner == null || assembly == null || assembly.VariantSet == null) return;

            var set = assembly.VariantSet;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RECAST CUSTOMIZER - MATERIAL NOTES");
            sb.AppendLine("asset       : " + set.assetName);
            sb.AppendLine("combination : " + assembly.GetCombinationCode());
            sb.AppendLine("written     : " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine();

            int changed = 0;
            for (int s = 0; s < set.segments.Count; s++)
            {
                var seg = set.segments[s];
                var look = _tuner.LookFor(s);
                if (look == null) continue;

                bool touched = _tuner.IsTouched(s);
                if (touched) changed++;

                sb.AppendLine(seg.DisplayLabel.ToUpperInvariant()
                              + "  (variant " + seg.VariantLabel(assembly.Selection[s]) + ")"
                              + (touched ? "   ** adjusted **" : "   unchanged"));
                sb.AppendLine("    hue          " + look.hue.ToString("+0.00;-0.00; 0.00"));
                sb.AppendLine("    saturation   " + look.saturation.ToString("0.00"));
                sb.AppendLine("    brightness   " + look.brightness.ToString("0.00"));
                sb.AppendLine("    smoothness   " + look.smoothness.ToString("0.00"));
                sb.AppendLine("    normal       " + look.normal.ToString("0.00"));
                sb.AppendLine();
            }

            sb.AppendLine(changed == 0
                ? "No material was adjusted in this session."
                : changed + " material(s) adjusted. Hue 0.00 and saturation/brightness 1.00 "
                  + "mean untouched; smoothness and normal are absolute values.");

            var text = sb.ToString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var fileName = set.assetName + "_notes_" + assembly.GetCombinationCode() + ".txt";

#if UNITY_WEBGL && !UNITY_EDITOR
            RecastDownloadFile(fileName, bytes, bytes.Length);
            Debug.Log("[GloveCustomizerUI] Notes sent to browser: " + fileName);
#else
            var dir = Path.Combine(Directory.GetCurrentDirectory(), screenshotFolder);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, fileName);
            File.WriteAllText(file, text);
            Debug.Log("[GloveCustomizerUI] Notes saved: " + file);
#endif
        }

        private IEnumerator CaptureScreenshot()
        {
            _root.style.display = DisplayStyle.None;
            yield return new WaitForEndOfFrame();

            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var png = tex.EncodeToPNG();
            Destroy(tex);

            var fileName = assembly.VariantSet.assetName + "_" + assembly.GetCombinationCode() + ".png";
#if UNITY_WEBGL && !UNITY_EDITOR
            RecastDownloadFile(fileName, png, png.Length);
            Debug.Log("[GloveCustomizerUI] Screenshot sent to browser: " + fileName);
#else
            var dir = Path.Combine(Directory.GetCurrentDirectory(), screenshotFolder);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, fileName);
            File.WriteAllBytes(file, png);
            Debug.Log("[GloveCustomizerUI] Screenshot saved: " + file);
#endif
            _root.style.display = DisplayStyle.Flex;
        }

        // ---------------------------------------------------------------- helpers

        private static void ResetThemeDefaults(VisualElement el)
        {
            el.style.marginLeft = 0; el.style.marginRight = 0;
            el.style.marginTop = 0; el.style.marginBottom = 0;
            el.style.minWidth = 0; el.style.minHeight = 0;
            el.style.paddingLeft = 0; el.style.paddingRight = 0;
            el.style.paddingTop = 0; el.style.paddingBottom = 0;
        }

        /// <summary>
        /// Centres a heading inside a panel whose rows are otherwise aligned to one edge.
        /// </summary>
        /// <summary>
        /// Swaps ordinary spaces for an en space in display text.
        ///
        /// This font kerns hard, and it kerns across the space too: "SWAPPABLE PARTS" keeps
        /// its gap while "TEXTURE SIZE" closes up, purely because of which letters sit either
        /// side. Word spacing does not reliably beat that, and the result looks like a typo.
        /// An en space is a fixed width the font will not kern away, so the gap survives
        /// whatever letters surround it.
        /// </summary>
        private static string Spaced(string text) =>
            string.IsNullOrEmpty(text) ? text : text.Replace(' ', ' ');

        private static Label Centred(Label l)
        {
            l.style.alignSelf = Align.Center;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            return l;
        }

        /// <summary>
        /// A panel heading with a quieter second line under it.
        ///
        /// The title names what the panel contains; the subtitle names what you do with it.
        /// One label could not carry both without one of them lying: this panel lists parts,
        /// but the thing you change is the material on each, and calling it either alone left
        /// half the reader guessing.
        /// </summary>
        private VisualElement SectionHeading(string title, string subtitle)
        {
            var box = new VisualElement();
            box.style.alignItems = Align.Center;
            box.style.alignSelf = Align.Center;
            box.style.marginBottom = U(10f);

            var head = SectionLabel(title);
            head.style.marginBottom = U(2f);
            box.Add(Centred(head));

            var sub = new Label(Spaced(subtitle));
            sub.style.color = new Color(TextMuted.r, TextMuted.g, TextMuted.b, 0.85f);
            sub.style.fontSize = S(BaseSection) * 0.82f;
            sub.style.letterSpacing = U(1.4f);
            sub.style.unityTextAlign = TextAnchor.MiddleCenter;
            ApplyFont(sub, false);
            box.Add(sub);

            return box;
        }

        private Label SectionLabel(string text)
        {
            var l = new Label(text);
            l.style.color = Gold;
            l.style.fontSize = S(BaseSection);
            l.style.letterSpacing = U(2f);
            l.style.wordSpacing = U(5f);
            l.style.marginBottom = U(10f);
            ApplyFont(l, true);
            return l;
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r; el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r; el.style.borderBottomRightRadius = r;
        }

        private static void SetBorderWidth(VisualElement el, float w)
        {
            el.style.borderTopWidth = w; el.style.borderBottomWidth = w;
            el.style.borderLeftWidth = w; el.style.borderRightWidth = w;
        }

        private static void SetBorderColor(VisualElement el, Color c)
        {
            el.style.borderTopColor = c; el.style.borderBottomColor = c;
            el.style.borderLeftColor = c; el.style.borderRightColor = c;
        }
    }
}
