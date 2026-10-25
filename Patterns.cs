using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// How a whole-sail pattern lies on the cloth: turned to run along the foot (a gaff's boom) or level, mirrored,
    /// scaled up and across and shifted as the player set it (Кэп, 0.4). Its own axes have the origin at the cloth's
    /// middle; the extents are the cloth's in those axes.
    /// </summary>
    internal sealed class PatternFrame
    {
        public float Cos = 1f, Sin;
        public float MinX, MaxX, MinY, MaxY;
        public float ScaleX = 1f, ScaleY = 1f, OffX, OffY;
        public bool Mirror;
        public float SpanX => MaxX - MinX;
        public float SpanY => MaxY - MinY;

        public static PatternFrame Of(ClothGeometry g, EmblemState st)
        {
            var f = new PatternFrame { Mirror = st.Mirror, ScaleX = Mathf.Max(0.05f, st.Stretch), ScaleY = Mathf.Max(0.05f, st.Size) };
            if (st.AlongFoot)
            {
                f.Cos = g.Foot.x;
                f.Sin = g.Foot.y;
            }
            f.MinX = f.MinY = float.MaxValue;
            f.MaxX = f.MaxY = float.MinValue;
            if (g.VX != null)
                for (int i = 0; i < g.VX.Length; i++)
                {
                    f.Turn(g.VX[i] - g.CenterX, g.VY[i] - g.CenterY, out float u, out float v);
                    f.MinX = Mathf.Min(f.MinX, u); f.MaxX = Mathf.Max(f.MaxX, u);
                    f.MinY = Mathf.Min(f.MinY, v); f.MaxY = Mathf.Max(f.MaxY, v);
                }
            if (f.MaxX - f.MinX < 1e-3f || f.MaxY - f.MinY < 1e-3f)
            {
                f.MinX = g.MinX - g.CenterX; f.MaxX = g.MaxX - g.CenterX;
                f.MinY = g.MinY - g.CenterY; f.MaxY = g.MaxY - g.CenterY;
            }
            f.OffX = st.OffsetX * f.SpanX;
            f.OffY = st.Offset * f.SpanY;
            return f;
        }

        private void Turn(float dx, float dy, out float u, out float v)
        {
            u = dx * Cos + dy * Sin;
            v = -dx * Sin + dy * Cos;
        }

        /// <summary>A point of the cloth (ClothGeometry's X, Y) in the pattern's own axes.</summary>
        public void Map(ClothGeometry g, float x, float y, out float u, out float v)
        {
            if (Mirror) x = 2f * g.CenterX - x;
            Turn(x - g.CenterX, y - g.CenterY, out u, out v);
            u = (u - OffX) / ScaleX;
            v = (v - OffY) / ScaleY;
        }
    }

    /// <summary>
    /// The built-in whole-sail patterns, drawn from the cloth's own shape in metres instead of stretching their
    /// pictures over the cloth's box (0.2.1): stripes and arms keep their width when the sail is made wider, splits and
    /// crosses meet at the middle of a triangular sail, the border follows the cloth's edge, the waves keep their size
    /// (Кэп). Widths are shares of the sail's height, as in the pictures (art/patterns.py), which stay as the
    /// thumbnails in the window. Players' own patterns are still stretched pictures.
    /// </summary>
    internal static class Patterns
    {
        /// <summary>Paint coverage 0..1 at cloth point (x, y), pattern point (u, v); false for a pattern drawn from its picture.</summary>
        internal static bool TryCoverage(string id, float x, float y, float u, float v, ClothGeometry g, PatternFrame f, float texelMetres, out float a)
        {
            if (!TryDistance(id, x, y, u, v, g, f, out float d))
            {
                a = 0f;
                return false;
            }
            // signed distance (pattern metres, inside negative) to coverage, smoothed over one texel where it is
            float metres = d * Mathf.Min(f.ScaleX, f.ScaleY);
            a = Mathf.Clamp01(0.5f - metres / Mathf.Max(1e-4f, texelMetres));
            return true;
        }

        // In the pattern's axes: the cloth's middle at 0, 0.
        private static bool TryDistance(string id, float x, float y, float u, float v, ClothGeometry g, PatternFrame f, out float d)
        {
            float h = f.SpanY;
            d = 0f;
            switch (id)
            {
                case "p:band_horizontal":
                    d = Mathf.Abs(v) - h / 6f;
                    return true;
                case "p:stripes_horizontal":
                {
                    // five bands from the head down, the 2nd and 4th painted
                    float b = h / 5f, top = f.MaxY;
                    d = Mathf.Min(Band(v, top - 2f * b, top - b), Band(v, top - 4f * b, top - 3f * b));
                    return true;
                }
                case "p:stripes_vertical":
                {
                    // stripes one seventh of the height wide, from the middle out, as many as the sail is wide
                    float b = h / 7f, q = u / b + 0.5f, k = Mathf.Floor(q), local = q - k;
                    float edge = Mathf.Min(local, 1f - local) * b;
                    d = Mathf.Abs(k) % 2f == 0f ? -edge : edge; // the middle one painted, as in the picture
                    return true;
                }
                case "p:half_top":
                    d = -v;
                    return true;
                case "p:quartered":
                {
                    float e = Mathf.Min(Mathf.Abs(u), Mathf.Abs(v));
                    d = (u < 0f) == (v > 0f) ? -e : e;
                    return true;
                }
                case "p:cross_full":
                    d = Mathf.Min(Mathf.Abs(u), Mathf.Abs(v)) - h / 10f;
                    return true;
                case "p:nordic_cross":
                    d = Mathf.Min(Mathf.Abs(u + 0.12f * f.SpanX), Mathf.Abs(v)) - h / 12f;
                    return true;
                case "p:saltire_full":
                {
                    // the diagonals of the cloth's box, through its middle
                    var d1 = new Vector2(f.SpanX, h).normalized;
                    var d2 = new Vector2(f.SpanX, -h).normalized;
                    d = Mathf.Min(Mathf.Abs(u * d1.y - v * d1.x), Mathf.Abs(u * d2.y - v * d2.x)) - h / 10f;
                    return true;
                }
                case "p:border":
                    // along the cloth's own edge: shifting does not apply, scaling makes it wider
                    d = (g.EdgeDistance(x, y) - h / 9f * f.ScaleY) / Mathf.Min(f.ScaleX, f.ScaleY);
                    return true;
                case "p:chevron":
                {
                    // a V from the upper corners down to the middle, a sixth of the height thick
                    float half = Mathf.Max(1e-3f, Mathf.Max(-f.MinX, f.MaxX));
                    float line = f.MaxY - 0.62f * h + 0.47f * h * Mathf.Min(1f, Mathf.Abs(u) / half);
                    d = Mathf.Max(v - line, line - h / 6f - v);
                    return true;
                }
                case "p:seigaiha":
                    d = Seigaiha(u - f.MinX, f.MaxY - v, h / 8f);
                    return true;
            }
            return false;
        }

        private static float Band(float y, float lo, float hi) => Mathf.Max(lo - y, y - hi);

        private static readonly float[] Rings = { 1f, 0.82f, 0.64f, 0.46f, 0.28f };

        // Rows of concentric arcs, each row half a radius below the one before and drawn over it, every other row
        // shifted by a radius; the rings alternate paint and canvas from the outside in.
        private static float Seigaiha(float x, float y, float r)
        {
            float step = 0.5f * r;
            int top = Mathf.FloorToInt((y + r) / step), bottom = Mathf.CeilToInt((y - r) / step);
            for (int j = top; j >= bottom; j--)
            {
                float off = (j & 1) != 0 ? r : 0f;
                float cxr = Mathf.Round((x - off) / (2f * r)) * 2f * r + off;
                float dx = x - cxr, dy = y - j * step;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist > r) continue;
                int k = Rings.Length - 1;
                while (k > 0 && dist > Rings[k] * r) k--;
                float outer = Rings[k] * r, inner = k + 1 < Rings.Length ? Rings[k + 1] * r : -1f;
                float e = inner < 0f ? outer - dist : Mathf.Min(outer - dist, dist - inner);
                return k % 2 == 0 ? -e : e;
            }
            return r * 0.1f; // between circles: canvas
        }
    }
}
