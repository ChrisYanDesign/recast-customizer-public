using UnityEngine;
using UnityEngine.UIElements;

namespace RecastCustomizer
{
    /// <summary>
    /// Drawn UI furniture for the equipment screen — ornate frames and attribute glyphs.
    ///
    /// Everything here is painted procedurally rather than shipped as sprites. That keeps
    /// the project free of art dependencies, scales crisply at any resolution, and lets the
    /// trim take its colour from the same palette as the rest of the screen.
    /// </summary>
    public static class CustomizerChrome
    {
        /// <summary>
        /// A chamfered plate with a double rule, corner scrollwork and a crest at the top
        /// edge — the vocabulary of an illuminated manuscript border rather than a div.
        ///
        /// Give it generous padding: the flourishes reach inward from every corner, and
        /// content set too close to the edge will collide with them.
        /// </summary>
        public sealed class OrnateFrame : VisualElement
        {
            public Color Trim { get; set; } = new(0.92f, 0.78f, 0.34f, 0.92f);
            public Color Fill { get; set; } = new(0.05f, 0.045f, 0.035f, 0.92f);

            /// <summary>Length of the chamfer cut across each corner.</summary>
            public float Cut { get; set; } = 15f;

            /// <summary>Gap between the outer and inner rule.</summary>
            public float Inset { get; set; } = 5f;

            /// <summary>Draw the corner scrollwork and top crest.</summary>
            public bool Flourishes { get; set; } = true;

            public OrnateFrame()
            {
                pickingMode = PickingMode.Position;
                generateVisualContent += Draw;
            }

            public void Refresh() => MarkDirtyRepaint();

            private void Draw(MeshGenerationContext ctx)
            {
                // The frame must trace the element's OUTER edge, not contentRect — that one
                // excludes padding, which would draw the rule right against the text it is
                // supposed to be framing.
                var r = new Rect(0f, 0f, resolvedStyle.width, resolvedStyle.height);
                if (r.width < 8f || r.height < 8f) return;

                var p = ctx.painter2D;
                float cut = Mathf.Min(Cut, Mathf.Min(r.width, r.height) * 0.26f);

                // ---- body ----
                p.fillColor = Fill;
                p.BeginPath();
                TracePlate(p, r, cut, 0f);
                p.ClosePath();
                p.Fill();

                // ---- outer rule ----
                p.lineWidth = 1.5f;
                p.lineJoin = LineJoin.Miter;
                p.strokeColor = Trim;
                p.BeginPath();
                TracePlate(p, r, cut, 0.8f);
                p.ClosePath();
                p.Stroke();

                // ---- inner hairline ----
                p.lineWidth = 0.8f;
                p.strokeColor = Fade(Trim, 0.40f);
                p.BeginPath();
                TracePlate(p, r, cut, Inset + 0.8f);
                p.ClosePath();
                p.Stroke();

                if (!Flourishes) return;

                // ---- corner scrollwork ----
                float reach = Mathf.Min(26f, Mathf.Min(r.width, r.height) * 0.22f);
                p.lineWidth = 1.1f;
                p.lineCap = LineCap.Round;
                p.strokeColor = Fade(Trim, 0.78f);

                Scroll(p, new Vector2(r.xMin + cut, r.yMin + 1f), new Vector2(1f, 1f), reach);
                Scroll(p, new Vector2(r.xMax - cut, r.yMin + 1f), new Vector2(-1f, 1f), reach);
                Scroll(p, new Vector2(r.xMin + cut, r.yMax - 1f), new Vector2(1f, -1f), reach);
                Scroll(p, new Vector2(r.xMax - cut, r.yMax - 1f), new Vector2(-1f, -1f), reach);

                // ---- crest, centred on the top rule ----
                Crest(p, new Vector2(r.center.x, r.yMin + 1f), Mathf.Min(11f, r.width * 0.07f));
            }

            /// <summary>Traces the chamfered outline, pulled in by <paramref name="pad"/>.</summary>
            private static void TracePlate(Painter2D p, Rect r, float cut, float pad)
            {
                float x0 = r.xMin + pad, x1 = r.xMax - pad;
                float y0 = r.yMin + pad, y1 = r.yMax - pad;
                float c = Mathf.Max(0f, cut - pad * 0.5f);

                p.MoveTo(new Vector2(x0 + c, y0));
                p.LineTo(new Vector2(x1 - c, y0));
                p.LineTo(new Vector2(x1, y0 + c));
                p.LineTo(new Vector2(x1, y1 - c));
                p.LineTo(new Vector2(x1 - c, y1));
                p.LineTo(new Vector2(x0 + c, y1));
                p.LineTo(new Vector2(x0, y1 - c));
                p.LineTo(new Vector2(x0, y0 + c));
            }

            /// <summary>
            /// A curling tendril running inward along the top or bottom edge, closing on a
            /// small terminal bead. <paramref name="d"/> gives the direction to sweep.
            /// </summary>
            private static void Scroll(Painter2D p, Vector2 origin, Vector2 d, float reach)
            {
                var a = origin;
                var b = new Vector2(origin.x + reach * d.x, origin.y);

                p.BeginPath();
                p.MoveTo(a);
                p.BezierCurveTo(
                    new Vector2(a.x + reach * 0.45f * d.x, a.y),
                    new Vector2(b.x - reach * 0.10f * d.x, a.y + reach * 0.34f * d.y),
                    new Vector2(b.x, a.y + reach * 0.40f * d.y));
                p.Stroke();

                // the inward hook
                p.BeginPath();
                p.MoveTo(new Vector2(a.x + reach * 0.18f * d.x, a.y + reach * 0.20f * d.y));
                p.BezierCurveTo(
                    new Vector2(a.x + reach * 0.52f * d.x, a.y + reach * 0.20f * d.y),
                    new Vector2(a.x + reach * 0.52f * d.x, a.y + reach * 0.60f * d.y),
                    new Vector2(a.x + reach * 0.20f * d.x, a.y + reach * 0.56f * d.y));
                p.Stroke();

                p.fillColor = p.strokeColor;
                p.BeginPath();
                p.Arc(new Vector2(b.x, a.y + reach * 0.40f * d.y), 1.6f, 0f, 360f);
                p.Fill();
            }

            /// <summary>A lozenge with two wings, sitting astride the top rule.</summary>
            private static void Crest(Painter2D p, Vector2 c, float s)
            {
                p.fillColor = p.strokeColor;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - s * 0.62f));
                p.LineTo(new Vector2(c.x + s * 0.42f, c.y));
                p.LineTo(new Vector2(c.x, c.y + s * 0.62f));
                p.LineTo(new Vector2(c.x - s * 0.42f, c.y));
                p.ClosePath();
                p.Fill();

                p.lineWidth = 1f;
                for (int i = -1; i <= 1; i += 2)
                {
                    p.BeginPath();
                    p.MoveTo(new Vector2(c.x + s * 0.52f * i, c.y));
                    p.BezierCurveTo(
                        new Vector2(c.x + s * 1.05f * i, c.y - s * 0.30f),
                        new Vector2(c.x + s * 1.55f * i, c.y + s * 0.12f),
                        new Vector2(c.x + s * 1.95f * i, c.y));
                    p.Stroke();
                }
            }

            private static Color Fade(Color c, float a) => new(c.r, c.g, c.b, c.a * a);
        }


        /// <summary>
        /// A wide horizontal strip with chamfered ends and a rule along each long edge —
        /// the same vocabulary as <see cref="OrnateFrame"/>, proportioned for a header or a
        /// control legend so the top and bottom of the screen read as one set.
        /// </summary>
        public sealed class OrnateBar : VisualElement
        {
            public Color Trim { get; set; } = new(0.95f, 0.80f, 0.30f, 0.85f);
            public Color Fill { get; set; } = new(0.045f, 0.040f, 0.030f, 0.86f);

            /// <summary>Chamfer across each end.</summary>
            public float Cut { get; set; } = 16f;

            /// <summary>Draw the centre lozenge and end beads.</summary>
            public bool Ornament { get; set; } = true;

            public OrnateBar()
            {
                pickingMode = PickingMode.Position;
                generateVisualContent += Draw;
            }

            public void Refresh() => MarkDirtyRepaint();

            private void Draw(MeshGenerationContext ctx)
            {
                var r = new Rect(0f, 0f, resolvedStyle.width, resolvedStyle.height);
                if (r.width < 16f || r.height < 6f) return;

                var p = ctx.painter2D;
                float cut = Mathf.Min(Cut, r.height * 0.75f);

                p.fillColor = Fill;
                p.BeginPath();
                Trace(p, r, cut, 0f);
                p.ClosePath();
                p.Fill();

                p.lineWidth = 1.4f;
                p.lineJoin = LineJoin.Miter;
                p.strokeColor = Trim;
                p.BeginPath();
                Trace(p, r, cut, 0.7f);
                p.ClosePath();
                p.Stroke();

                // a hairline inside the long edges only — keeps the ends clean
                p.lineWidth = 0.7f;
                p.strokeColor = new Color(Trim.r, Trim.g, Trim.b, Trim.a * 0.38f);
                p.BeginPath();
                p.MoveTo(new Vector2(r.xMin + cut + 4f, r.yMin + 4.5f));
                p.LineTo(new Vector2(r.xMax - cut - 4f, r.yMin + 4.5f));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(r.xMin + cut + 4f, r.yMax - 4.5f));
                p.LineTo(new Vector2(r.xMax - cut - 4f, r.yMax - 4.5f));
                p.Stroke();

                if (!Ornament) return;

                p.fillColor = Trim;
                Bead(p, new Vector2(r.xMin + cut * 0.55f, r.center.y), 2.2f);
                Bead(p, new Vector2(r.xMax - cut * 0.55f, r.center.y), 2.2f);
            }

            private static void Trace(Painter2D p, Rect r, float cut, float pad)
            {
                float x0 = r.xMin + pad, x1 = r.xMax - pad;
                float y0 = r.yMin + pad, y1 = r.yMax - pad;
                float c = Mathf.Max(0f, cut - pad);

                p.MoveTo(new Vector2(x0 + c, y0));
                p.LineTo(new Vector2(x1 - c, y0));
                p.LineTo(new Vector2(x1, r.center.y));
                p.LineTo(new Vector2(x1 - c, y1));
                p.LineTo(new Vector2(x0 + c, y1));
                p.LineTo(new Vector2(x0, r.center.y));
            }

            private static void Bead(Painter2D p, Vector2 c, float s)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - s));
                p.LineTo(new Vector2(c.x + s, c.y));
                p.LineTo(new Vector2(c.x, c.y + s));
                p.LineTo(new Vector2(c.x - s, c.y));
                p.ClosePath();
                p.Fill();
            }
        }

        /// <summary>Which emblem an <see cref="IconGlyph"/> draws.</summary>
        public enum Glyph { Shield, Ring, Wing, Hourglass, Rosette, Feather, Lock, ArrowLeft, ArrowRight }

        /// <summary>
        /// A small line-drawn emblem. Seven shapes cover the falconry attributes and the
        /// locked state without needing an icon font or an atlas.
        /// </summary>
        public sealed class IconGlyph : VisualElement
        {
            public Glyph Shape { get; set; } = Glyph.Shield;
            public Color Tint { get; set; } = Color.white;

            /// <summary>Multiplies the stroke weight. Arrows want more than emblems do.</summary>
            public float Weight { get; set; } = 1f;

            public IconGlyph()
            {
                pickingMode = PickingMode.Ignore;
                generateVisualContent += Draw;
            }

            public void Refresh() => MarkDirtyRepaint();

            private void Draw(MeshGenerationContext ctx)
            {
                var r = contentRect;
                if (r.width < 3f || r.height < 3f) return;

                float s = Mathf.Min(r.width, r.height);
                var c = r.center;
                var p = ctx.painter2D;
                p.lineWidth = Mathf.Max(1f, s * 0.085f * Weight);
                p.strokeColor = Tint;
                p.lineJoin = LineJoin.Round;
                p.lineCap = LineCap.Round;

                switch (Shape)
                {
                    case Glyph.Shield: Shield(p, c, s); break;
                    case Glyph.Ring: Ring(p, c, s); break;
                    case Glyph.Wing: Wing(p, c, s); break;
                    case Glyph.Hourglass: Hourglass(p, c, s); break;
                    case Glyph.Rosette: Rosette(p, c, s); break;
                    case Glyph.Feather: Feather(p, c, s); break;
                    case Glyph.Lock: Padlock(p, c, s); break;
                    case Glyph.ArrowLeft: Chevron(p, c, s, -1f); break;
                    case Glyph.ArrowRight: Chevron(p, c, s, 1f); break;
                }
            }

            private static void Shield(Painter2D p, Vector2 c, float s)
            {
                float w = s * 0.30f, h = s * 0.36f;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - w, c.y - h));
                p.LineTo(new Vector2(c.x + w, c.y - h));
                p.LineTo(new Vector2(c.x + w, c.y + h * 0.18f));
                p.LineTo(new Vector2(c.x, c.y + h));
                p.LineTo(new Vector2(c.x - w, c.y + h * 0.18f));
                p.ClosePath();
                p.Stroke();
            }

            private static void Ring(Painter2D p, Vector2 c, float s)
            {
                p.BeginPath();
                p.Arc(c, s * 0.30f, 0f, 360f);
                p.Stroke();
                p.BeginPath();
                p.Arc(c, s * 0.15f, 0f, 360f);
                p.Stroke();
            }

            private static void Wing(Painter2D p, Vector2 c, float s)
            {
                float w = s * 0.34f, h = s * 0.22f;
                for (int i = 0; i < 3; i++)
                {
                    float o = (i - 1) * h * 0.62f;
                    p.BeginPath();
                    p.MoveTo(new Vector2(c.x - w, c.y + o + h * 0.42f));
                    p.LineTo(new Vector2(c.x + w * 0.30f, c.y + o - h * 0.16f));
                    p.LineTo(new Vector2(c.x + w, c.y + o + h * 0.10f));
                    p.Stroke();
                }
            }

            private static void Hourglass(Painter2D p, Vector2 c, float s)
            {
                float w = s * 0.24f, h = s * 0.32f;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - w, c.y - h));
                p.LineTo(new Vector2(c.x + w, c.y - h));
                p.LineTo(new Vector2(c.x - w, c.y + h));
                p.LineTo(new Vector2(c.x + w, c.y + h));
                p.ClosePath();
                p.Stroke();
            }

            private static void Rosette(Painter2D p, Vector2 c, float s)
            {
                float outer = s * 0.34f, inner = s * 0.15f;
                p.BeginPath();
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.PI / 6f - Mathf.PI * 0.5f;
                    float rad = (i % 2 == 0) ? outer : inner;
                    var pt = new Vector2(c.x + Mathf.Cos(a) * rad, c.y + Mathf.Sin(a) * rad);
                    if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                }
                p.ClosePath();
                p.Stroke();
            }

            private static void Feather(Painter2D p, Vector2 c, float s)
            {
                float h = s * 0.34f;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - h));
                p.LineTo(new Vector2(c.x, c.y + h));
                p.Stroke();

                for (int i = 0; i < 4; i++)
                {
                    float t = i / 3f;
                    float y = Mathf.Lerp(c.y - h * 0.78f, c.y + h * 0.42f, t);
                    float w = Mathf.Lerp(s * 0.10f, s * 0.26f, t);
                    p.BeginPath();
                    p.MoveTo(new Vector2(c.x, y));
                    p.LineTo(new Vector2(c.x - w, y + w * 0.55f));
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(new Vector2(c.x, y));
                    p.LineTo(new Vector2(c.x + w, y + w * 0.55f));
                    p.Stroke();
                }
            }


            /// <summary>
            /// A chevron with a small bar behind it, echoing the beads on the frames so the
            /// carousel controls belong to the same set as everything else.
            /// </summary>
            /// <summary>
            /// A double chevron pointing along <paramref name="dir"/>. The apex leads and
            /// the shoulders trail it — getting those the wrong way round draws an arrow
            /// that points back at the thing it is supposed to be leading away from.
            /// </summary>
            private static void Chevron(Painter2D p, Vector2 c, float s, float dir)
            {
                float w = s * 0.17f, h = s * 0.26f;

                // leading chevron, then a lighter one trailing behind it
                Wedge(p, new Vector2(c.x + w * 0.85f * dir, c.y), w, h, dir);

                p.lineWidth *= 0.68f;
                Wedge(p, new Vector2(c.x - w * 1.15f * dir, c.y), w, h * 0.66f, dir);
            }

            private static void Wedge(Painter2D p, Vector2 at, float w, float h, float dir)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(at.x - w * dir, at.y - h));
                p.LineTo(new Vector2(at.x + w * dir, at.y));
                p.LineTo(new Vector2(at.x - w * dir, at.y + h));
                p.Stroke();
            }

            private static void Padlock(Painter2D p, Vector2 c, float s)
            {
                float w = s * 0.24f, h = s * 0.20f;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - w, c.y + h));
                p.LineTo(new Vector2(c.x + w, c.y + h));
                p.LineTo(new Vector2(c.x + w, c.y - h * 0.30f));
                p.LineTo(new Vector2(c.x - w, c.y - h * 0.30f));
                p.ClosePath();
                p.Stroke();

                p.BeginPath();
                p.Arc(new Vector2(c.x, c.y - h * 0.34f), w * 0.62f, 180f, 360f);
                p.Stroke();
            }
        }

        /// <summary>Maps an attribute's position in the list to a sensible emblem.</summary>
        public static Glyph GlyphForAttribute(int index) => index switch
        {
            0 => Glyph.Shield,     // protection
            1 => Glyph.Ring,       // bond
            2 => Glyph.Wing,       // agility
            3 => Glyph.Hourglass,  // endurance
            4 => Glyph.Rosette,    // prestige
            _ => Glyph.Feather
        };
    }
}
