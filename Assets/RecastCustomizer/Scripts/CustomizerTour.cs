using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecastCustomizer
{
    /// <summary>
    /// A first-run walkthrough: a dimmed screen with one panel lit at a time and a bubble
    /// explaining it.
    ///
    /// Worth doing because this interface is dense. Four panels, a carousel, five sliders and
    /// two export paths is a lot to meet at once, and a portfolio piece is usually opened by
    /// someone with no context and very little patience. Thirty seconds of guidance is the
    /// difference between "I see what this does" and a closed tab.
    ///
    /// The dimming is built from four rectangles around the highlighted element rather than a
    /// shader cutout. Same result, no extra material, and it works on any panel shape.
    /// </summary>
    public class CustomizerTour
    {
        public class Step
        {
            public string Title;
            public string Body;
            public Func<VisualElement> Target;   // resolved late: panels move as the window resizes
            public Func<List<VisualElement>> Extras;  // ringed, but not cut out of the shade
            public Action OnEnter;               // e.g. pull the camera back so markers fit
        }

        private const string SeenKey = "RecastCustomizer.TourSeen";

        private readonly VisualElement _root;
        private readonly List<Step> _steps = new();
        private readonly Func<float, float> _scale;   // the UI's own scaling, so text matches
        private readonly Font _display;

        private VisualElement _layer;
        private VisualElement _bubble;
        private Label _title, _body, _counter;
        private Button _next, _back;
        private readonly VisualElement[] _shade = new VisualElement[4];
        private readonly List<VisualElement> _extraRings = new();
        private VisualElement _ring;

        private int _index = -1;      // -1 is the welcome panel
        private bool _running;
        private EventCallback<GeometryChangedEvent> _reflow;

        private string _welcomeTitle = "RECAST CUSTOMIZER";
        private string _welcomeBody = "";

        public void SetWelcome(string title, string body)
        {
            _welcomeTitle = title;
            _welcomeBody = body;
        }

        public bool IsRunning => _running;
        public event Action Finished;

        public CustomizerTour(VisualElement root, Func<float, float> scale, Font displayFont)
        {
            _root = root;
            _scale = scale;
            _display = displayFont;
        }

        public void AddStep(string title, string body, Func<VisualElement> target,
                            Func<List<VisualElement>> extras = null, Action onEnter = null)
            => _steps.Add(new Step
            {
                Title = title, Body = body, Target = target, Extras = extras, OnEnter = onEnter
            });

        /// <summary>True the first time this machine opens the tool.</summary>
        public static bool NeverSeen => PlayerPrefs.GetInt(SeenKey, 0) == 0;

        public static void ForgetSeen() => PlayerPrefs.DeleteKey(SeenKey);

        public void Start()
        {
            if (_steps.Count == 0) return;

            // Marked as seen on opening rather than on finishing. Someone who leaves halfway
            // has still met it, and showing it again every session would be nagging.
            PlayerPrefs.SetInt(SeenKey, 1);
            PlayerPrefs.Save();

            // Tear down any previous run first. Replaying used to leave the old overlay in
            // the hierarchy with its own buttons and its own resize callback still attached,
            // so layers stacked up and stray clicks reached the wrong one.
            Teardown();

            _running = true;
            _index = -1;
            Build();
            Show();
        }

        public void Stop()
        {
            _running = false;
            PlayerPrefs.SetInt(SeenKey, 1);
            PlayerPrefs.Save();
            Teardown();
            Finished?.Invoke();
        }

        private void Teardown()
        {
            if (_reflow != null)
            {
                _root.UnregisterCallback(_reflow);
                _reflow = null;
            }
            _layer?.RemoveFromHierarchy();
            _layer = null;
            _extraRings.Clear();
        }

        // ---------------------------------------------------------------- construction

        private void Build()
        {
            _layer = new VisualElement();
            _layer.style.position = Position.Absolute;
            _layer.style.left = 0; _layer.style.top = 0;
            _layer.style.right = 0; _layer.style.bottom = 0;
            // Swallows clicks: while the tour is up, the interface underneath should not
            // react. Someone following instructions should not set something by accident.
            _layer.pickingMode = PickingMode.Position;
            _root.Add(_layer);

            for (int i = 0; i < 4; i++)
            {
                _shade[i] = new VisualElement();
                _shade[i].style.position = Position.Absolute;
                _shade[i].style.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 0.78f);
                _layer.Add(_shade[i]);
            }

            _ring = new VisualElement();
            _ring.style.position = Position.Absolute;
            _ring.pickingMode = PickingMode.Ignore;
            SetBorder(_ring, 2, new Color(0.96f, 0.82f, 0.34f, 0.95f));
            _layer.Add(_ring);

            _bubble = new VisualElement();
            _bubble.style.position = Position.Absolute;
            // Placed once from an estimated height, then again once it has actually measured
            // itself. A long step is taller than the guess, and without the second pass it
            // hangs off the bottom of the screen with its buttons out of reach.
            _bubble.RegisterCallback<GeometryChangedEvent>(_ => Reposition());
            _bubble.style.maxWidth = _scale(330f);
            _bubble.style.paddingLeft = _scale(22f);
            _bubble.style.paddingRight = _scale(22f);
            _bubble.style.paddingTop = _scale(18f);
            _bubble.style.paddingBottom = _scale(18f);
            _bubble.style.backgroundColor = new Color(0.055f, 0.050f, 0.038f, 0.98f);
            SetBorder(_bubble, 1, new Color(0.96f, 0.82f, 0.34f, 0.55f));
            _layer.Add(_bubble);

            _title = MakeLabel(22f, new Color(0.96f, 0.82f, 0.34f, 1f), true);
            _title.style.marginBottom = _scale(8f);
            _bubble.Add(_title);

            _body = MakeLabel(15f, new Color(0.94f, 0.90f, 0.80f, 1f), false);
            _body.style.whiteSpace = WhiteSpace.Normal;
            _body.style.marginBottom = _scale(16f);
            _bubble.Add(_body);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            _bubble.Add(row);

            _counter = MakeLabel(12f, new Color(0.68f, 0.63f, 0.51f, 1f), false);
            _counter.style.flexGrow = 1f;
            row.Add(_counter);

            _back = MakeButton("BACK", () => Go(_index - 1));
            row.Add(_back);

            _next = MakeButton("NEXT", () => Go(_index + 1));
            row.Add(_next);

            var skip = MakeButton("SKIP", Stop);
            skip.style.marginLeft = _scale(4f);
            row.Add(skip);

            // Panels are laid out after this runs, and they move when the window resizes, so
            // the highlight is positioned from a callback rather than measured once. The
            // callback is held so it can be removed again when the tour closes.
            _reflow = _ => Show();
            _root.RegisterCallback(_reflow);
        }

        private Label MakeLabel(float size, Color colour, bool display)
        {
            var l = new Label();
            l.style.color = colour;
            l.style.fontSize = _scale(size);
            l.style.letterSpacing = _scale(1.1f);
            l.style.wordSpacing = _scale(6f);
            if (display && _display != null)
                l.style.unityFontDefinition = FontDefinition.FromFont(_display);
            return l;
        }

        private Button MakeButton(string text, Action onClick)
        {
            var b = new Button(() => onClick())
            {
                text = text
            };
            b.style.marginLeft = _scale(6f);
            b.style.marginRight = 0;
            b.style.marginTop = 0;
            b.style.marginBottom = 0;
            b.style.paddingLeft = _scale(14f);
            b.style.paddingRight = _scale(14f);
            b.style.paddingTop = _scale(5f);
            b.style.paddingBottom = _scale(6f);
            b.style.fontSize = _scale(13f);
            b.style.letterSpacing = _scale(1.4f);
            b.style.color = new Color(0.96f, 0.82f, 0.34f, 1f);
            b.style.backgroundColor = new Color(0.96f, 0.82f, 0.34f, 0.10f);
            SetBorder(b, 1, new Color(0.96f, 0.82f, 0.34f, 0.45f));
            if (_display != null) b.style.unityFontDefinition = FontDefinition.FromFont(_display);
            return b;
        }

        private static void SetBorder(VisualElement el, float w, Color c)
        {
            el.style.borderTopWidth = w; el.style.borderRightWidth = w;
            el.style.borderBottomWidth = w; el.style.borderLeftWidth = w;
            el.style.borderTopColor = c; el.style.borderRightColor = c;
            el.style.borderBottomColor = c; el.style.borderLeftColor = c;
        }

        // ---------------------------------------------------------------- steps

        private void Go(int index)
        {
            if (index >= _steps.Count) { Stop(); return; }
            _index = Mathf.Max(-1, index);
            Show();
        }

        private void Show()
        {
            if (!_running || _layer == null) return;

            bool welcome = _index < 0;
            var step = welcome ? null : _steps[_index];

            // The display font kerns across the space itself, closing gaps depending on
            // which letters sit either side. An en space is a fixed width it will not kern.
            var titleText = welcome ? _welcomeTitle : step.Title;
            _title.text = string.IsNullOrEmpty(titleText)
                ? titleText : titleText.Replace(' ', ' ');
            _body.text = welcome ? _welcomeBody : step.Body;

            _counter.text = welcome ? "" : (_index + 1) + " / " + _steps.Count;
            _back.style.display = welcome || _index == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _next.text = welcome ? "START" : (_index == _steps.Count - 1 ? "FINISH" : "NEXT");

            var target = welcome ? null : step.Target?.Invoke();
            var hole = target != null ? target.worldBound : new Rect(0, 0, 0, 0);
            bool hasHole = target != null && hole.width > 1f && hole.height > 1f;

            LayoutShade(hasHole ? hole : new Rect(-10, -10, 0, 0));

            if (hasHole)
            {
                float pad = _scale(6f);
                _ring.style.display = DisplayStyle.Flex;
                _ring.style.left = hole.xMin - pad;
                _ring.style.top = hole.yMin - pad;
                _ring.style.width = hole.width + pad * 2f;
                _ring.style.height = hole.height + pad * 2f;
            }
            else _ring.style.display = DisplayStyle.None;

            ShowExtras(welcome ? null : step.Extras?.Invoke());
            PlaceBubble(hasHole ? hole : (Rect?)null, target);
            if (!welcome) step.OnEnter?.Invoke();
        }

        /// <summary>
        /// Rings drawn around secondary elements. They are not cut out of the shade, so they
        /// read as "and these relate to it" rather than "look here instead".
        /// </summary>
        private void ShowExtras(List<VisualElement> extras)
        {
            int n = extras?.Count ?? 0;
            while (_extraRings.Count < n)
            {
                var r = new VisualElement();
                r.style.position = Position.Absolute;
                r.pickingMode = PickingMode.Ignore;
                SetBorder(r, 2, new Color(0.96f, 0.82f, 0.34f, 0.85f));
                r.style.borderTopLeftRadius = 999; r.style.borderTopRightRadius = 999;
                r.style.borderBottomLeftRadius = 999; r.style.borderBottomRightRadius = 999;
                _layer.Add(r);
                _extraRings.Add(r);
            }

            for (int i = 0; i < _extraRings.Count; i++)
            {
                bool on = i < n && extras[i] != null && extras[i].worldBound.width > 1f;
                _extraRings[i].style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                if (!on) continue;

                var b = extras[i].worldBound;
                float pad = _scale(7f);
                _extraRings[i].style.left = b.xMin - pad;
                _extraRings[i].style.top = b.yMin - pad;
                _extraRings[i].style.width = b.width + pad * 2f;
                _extraRings[i].style.height = b.height + pad * 2f;
            }
        }

        /// <summary>Re-run only the placement, using the bubble's real measured size.</summary>
        private void Reposition()
        {
            if (!_running || _layer == null) return;
            bool welcome = _index < 0;
            var target = welcome ? null : _steps[_index].Target?.Invoke();
            var hole = target != null ? target.worldBound : new Rect(0, 0, 0, 0);
            bool hasHole = target != null && hole.width > 1f && hole.height > 1f;
            PlaceBubble(hasHole ? hole : (Rect?)null, target);
        }

        /// <summary>Four rectangles around the hole, which together dim everything else.</summary>
        private void LayoutShade(Rect hole)
        {
            float w = _root.worldBound.width, h = _root.worldBound.height;
            float pad = _scale(6f);
            float x0 = hole.xMin - pad, y0 = hole.yMin - pad;
            float x1 = hole.xMax + pad, y1 = hole.yMax + pad;

            Set(_shade[0], 0, 0, w, Mathf.Max(0, y0));                      // above
            Set(_shade[1], 0, y1, w, Mathf.Max(0, h - y1));                 // below
            Set(_shade[2], 0, y0, Mathf.Max(0, x0), Mathf.Max(0, y1 - y0)); // left
            Set(_shade[3], x1, y0, Mathf.Max(0, w - x1), Mathf.Max(0, y1 - y0)); // right
        }

        private static void Set(VisualElement el, float x, float y, float w, float h)
        {
            el.style.left = x; el.style.top = y;
            el.style.width = w; el.style.height = h;
        }

        /// <summary>
        /// Put the bubble on the opposite side of the screen from the thing it describes, so
        /// it never covers what it is pointing at.
        /// </summary>
        private void PlaceBubble(Rect? hole, VisualElement target)
        {
            float w = _root.worldBound.width, h = _root.worldBound.height;
            float bw = _bubble.resolvedStyle.width > 1f ? _bubble.resolvedStyle.width : _scale(330f);
            float bh = _bubble.resolvedStyle.height > 1f ? _bubble.resolvedStyle.height : _scale(180f);
            float gap = _scale(24f);

            if (hole == null)
            {
                _bubble.style.left = (w - bw) * 0.5f;
                _bubble.style.top = (h - bh) * 0.5f;
                return;
            }

            var r = hole.Value;
            float x, y;

            // A element that spans most of the width has no "beside" to sit in, so the
            // bubble goes above or below it instead. Everything else gets the opposite side
            // of the screen, so the bubble never covers what it is describing.
            if (r.width > w * 0.55f)
            {
                x = r.center.x - bw * 0.5f;
                y = r.yMin > h * 0.5f ? r.yMin - bh - gap : r.yMax + gap;
            }
            else
            {
                if (r.center.x > w * 0.5f) x = r.xMin - bw - gap;
                else x = r.xMax + gap;
                y = r.center.y - bh * 0.5f;
            }

            if (x < gap) x = gap;
            if (x + bw > w - gap) x = w - gap - bw;
            if (y < gap) y = gap;
            if (y + bh > h - gap) y = h - gap - bh;

            _bubble.style.left = x;
            _bubble.style.top = y;
        }
    }
}
