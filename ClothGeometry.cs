using System;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>
    /// Where each texel of a sail's canvas lies on the sail, in metres: X to the right of someone reading it, Y up the
    /// sail. The canvas covers the cloth's UV rectangle (s, t 0..1 across it).
    ///
    /// 0.2.1 mapped UV to metres with one linear fit, which is right only while the texture lies flat and even on the
    /// cloth. The fin sail's does not (its stripes came out as blobs), and on gaffs and the topsail gaff the fit skewed
    /// splits and crosses (Кэп). Now every triangle of the cloth is drawn into the canvas with its corners' own
    /// positions; texels off the cloth keep the fit, so filtering at the edges blends into something close.
    /// A coarse map of the cloth in X, Y gives the distance to its edge (border pattern, emblems kept on the cloth).
    /// </summary>
    internal sealed class ClothGeometry
    {
        public int W, H;
        public float[] X, Y;
        public float MinX, MaxX, MinY, MaxY;
        public float CenterX, CenterY;  // area-weighted centroid: the middle of a triangle, not of its box
        public float CenterS, CenterT;
        public float MetresPerTexel;
        public string Info;
        public float[] VX, VY;          // the cloth's vertices
        public string FrontSide = "unknown"; // where the cloth's front faces look
        public Vector2 Foot = Vector2.right; // along the foot of the sail (its lower edge), pointing right

        private float mapX0, mapY0, cell;
        private int mapW, mapH;
        private float[] edge; // metres to the nearest point off the cloth, 0 off it

        public float SpanX => MaxX - MinX;
        public float SpanY => MaxY - MinY;

        /// <summary>Metres from (x, y) to the edge of the cloth; 0 or less off it.</summary>
        public float EdgeDistance(float x, float y)
        {
            float fx = (x - mapX0) / cell - 0.5f, fy = (y - mapY0) / cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            float a = Edge(x0, y0), b = Edge(x0 + 1, y0), c = Edge(x0, y0 + 1), d = Edge(x0 + 1, y0 + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private float Edge(int x, int y) => x < 0 || y < 0 || x >= mapW || y >= mapH ? -cell : edge[y * mapW + x];

        // Least squares world = m + A*(s-ms) + B*(t-mt): A, B are metres per s and per t, and the sail's plane.
        private static bool Fit(Vector2[] uv, Vector3[] verts, Rect rect, Matrix4x4 toWorld, out Vector2[] st, out Vector3[] w,
                                out Vector3 A, out Vector3 B, out Vector3 mw, out float ms, out float mt)
        {
            int n = Mathf.Min(uv.Length, verts.Length);
            st = new Vector2[n];
            w = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                st[i] = new Vector2((uv[i].x - rect.xMin) / rect.width, (uv[i].y - rect.yMin) / rect.height);
                w[i] = toWorld.MultiplyPoint3x4(verts[i]);
            }
            mw = Vector3.zero;
            ms = 0f; mt = 0f;
            for (int i = 0; i < n; i++) { mw += w[i]; ms += st[i].x; mt += st[i].y; }
            if (n > 0) { mw /= n; ms /= n; mt /= n; }
            float sss = 0f, stt = 0f, sst = 0f;
            Vector3 sws = Vector3.zero, swt = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                float ds = st[i].x - ms, dt = st[i].y - mt;
                Vector3 dw = w[i] - mw;
                sss += ds * ds; stt += dt * dt; sst += ds * dt;
                sws += dw * ds; swt += dw * dt;
            }
            float det = sss * stt - sst * sst;
            A = Vector3.right * 5f; B = Vector3.up * 5f; // a 5 m square, for a cloth that cannot be measured
            bool fitted = n >= 3 && Mathf.Abs(det) > 1e-9f;
            if (fitted)
            {
                A = (sws * stt - swt * sst) / det;
                B = (swt * sss - sws * sst) / det;
                fitted = A.magnitude > 1e-4f && B.magnitude > 1e-4f && Vector3.Cross(A, B).magnitude > 1e-8f;
                if (!fitted) { A = Vector3.right * 5f; B = Vector3.up * 5f; }
            }
            return fitted;
        }

        // Canvas size from the metres it covers: the lateen is 16 x 20 m, and 1024 texels across it showed steps (Кэп).
        // (1.0.0 sized it for the most stretched part of a warped layout instead: bigger textures, the game froze
        // for half a second on every click at the shipyard, Кэп. The warped fin sail is not painted at all now.)
        private static int Side(Vector3 perUnit, float texelsPerMetre, int maxRes) =>
            Mathf.Clamp(Pow2(Mathf.RoundToInt(perUnit.magnitude * texelsPerMetre)), 256, maxRes);

        /// <summary>The canvas size Measure will come to, without the rest: the game's canvas is read at it first.</summary>
        public static void Size(Vector2[] uv, Vector3[] verts, Rect rect, Matrix4x4 toWorld, float texelsPerMetre, int maxRes, out int w, out int h)
        {
            Fit(uv, verts, rect, toWorld, out _, out _, out Vector3 A, out Vector3 B, out _, out _, out _);
            w = Side(A, texelsPerMetre, maxRes);
            h = Side(B, texelsPerMetre, maxRes);
        }

        /// <summary>
        /// Pure arithmetic on copies, no Unity objects: runs off the main thread (1.0.1). The ship's forward and
        /// starboard directions come in as vectors for that reason.
        /// </summary>
        public static ClothGeometry Measure(Vector2[] uv, Vector3[] verts, int[] tris, Rect rect, Matrix4x4 toWorld, Vector3 fwd, Vector3 starboard,
                                            float texelsPerMetre, int maxRes)
        {
            var g = new ClothGeometry();
            bool fitted = Fit(uv, verts, rect, toWorld, out Vector2[] st, out Vector3[] w, out Vector3 A, out Vector3 B,
                              out Vector3 mw, out float ms, out float mt);
            int n = st.Length;
            Vector3 normal = Vector3.Cross(A, B).normalized;

            // Up: the world's, laid into the sail's plane (a jib leans).
            Vector3 up = Vector3.up - normal * Vector3.Dot(normal, Vector3.up);
            up = up.magnitude > 0.2f ? up.normalized : B.normalized;
            // Right as read by someone on board: a square sail seen from aft looking forward, a fore-and-aft sail seen
            // from starboard. 0.1 always used the ship's right, which lies along a gaff only by chance, and took
            // transform.root for the ship, which need not be the boat.
            Vector3 view = Mathf.Abs(Vector3.Dot(normal, fwd)) >= Mathf.Abs(Vector3.Dot(normal, starboard)) ? fwd : -starboard;
            Vector3 right = Vector3.Cross(up, normal);
            if (Vector3.Dot(right, Vector3.Cross(Vector3.up, view)) < 0f) right = -right;

            // Each vertex where it really is.
            var vx = new float[n];
            var vy = new float[n];
            g.MinX = g.MinY = float.MaxValue;
            g.MaxX = g.MaxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = w[i] - mw;
                vx[i] = Vector3.Dot(d, right);
                vy[i] = Vector3.Dot(d, up);
                g.MinX = Mathf.Min(g.MinX, vx[i]); g.MaxX = Mathf.Max(g.MaxX, vx[i]);
                g.MinY = Mathf.Min(g.MinY, vy[i]); g.MaxY = Mathf.Max(g.MaxY, vy[i]);
            }
            if (n == 0 || g.SpanX < 1e-3f || g.SpanY < 1e-3f)
            {
                g.MinX = -2.5f; g.MaxX = 2.5f; g.MinY = -2.5f; g.MaxY = 2.5f;
            }

            // middle of the cloth
            double area = 0, cx = 0, cy = 0, cs = 0, ct = 0;
            if (tris != null)
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                    if (a >= n || b >= n || c >= n) continue;
                    double ar = Math.Abs((vx[b] - vx[a]) * (vy[c] - vy[a]) - (vx[c] - vx[a]) * (vy[b] - vy[a])) * 0.5;
                    area += ar;
                    cx += ar * (vx[a] + vx[b] + vx[c]) / 3.0;
                    cy += ar * (vy[a] + vy[b] + vy[c]) / 3.0;
                    cs += ar * (st[a].x + st[b].x + st[c].x) / 3.0;
                    ct += ar * (st[a].y + st[b].y + st[c].y) / 3.0;
                }
            if (area > 1e-6)
            {
                g.CenterX = (float)(cx / area); g.CenterY = (float)(cy / area);
                g.CenterS = (float)(cs / area); g.CenterT = (float)(ct / area);
            }
            else
            {
                g.CenterX = (g.MinX + g.MaxX) * 0.5f; g.CenterY = (g.MinY + g.MaxY) * 0.5f;
                g.CenterS = g.CenterT = 0.5f;
            }

            g.W = Side(A, texelsPerMetre, maxRes);
            g.H = Side(B, texelsPerMetre, maxRes);
            g.MetresPerTexel = Mathf.Max(A.magnitude / g.W, B.magnitude / g.H);

            // Texels: the fit everywhere, then each triangle with its own corners.
            int count = g.W * g.H;
            g.X = new float[count];
            g.Y = new float[count];
            float xs = Vector3.Dot(A, right), xt = Vector3.Dot(B, right), ys = Vector3.Dot(A, up), yt = Vector3.Dot(B, up);
            for (int y = 0; y < g.H; y++)
            {
                float t = (y + 0.5f) / g.H - mt;
                for (int x = 0; x < g.W; x++)
                {
                    float s = (x + 0.5f) / g.W - ms;
                    g.X[y * g.W + x] = xs * s + xt * t;
                    g.Y[y * g.W + x] = ys * s + yt * t;
                }
            }
            int drawn = 0;
            if (tris != null)
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                    if (a >= n || b >= n || c >= n) continue;
                    drawn += Raster(g.W, g.H,
                        st[a].x * g.W - 0.5f, st[a].y * g.H - 0.5f, st[b].x * g.W - 0.5f, st[b].y * g.H - 0.5f,
                        st[c].x * g.W - 0.5f, st[c].y * g.H - 0.5f, 0.5f,
                        (i, l0, l1, l2) =>
                        {
                            g.X[i] = l0 * vx[a] + l1 * vx[b] + l2 * vx[c];
                            g.Y[i] = l0 * vy[a] + l1 * vy[b] + l2 * vy[c];
                        });
                }

            // Which way the cloth's front faces look (Unity: Cross(b - a, c - a) of a front-facing triangle): a one-sided
            // layer is seen from that side only.
            Vector3 face = Vector3.zero;
            if (tris != null)
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                    if (a < n && b < n && c < n) face += Vector3.Cross(w[b] - w[a], w[c] - w[a]);
                }
            float df = Vector3.Dot(face, fwd), dsb = Vector3.Dot(face, starboard);
            g.FrontSide = face.sqrMagnitude < 1e-12f ? "unknown"
                : Mathf.Abs(df) >= Mathf.Abs(dsb) ? (df > 0f ? "forward (seen from ahead)" : "aft (seen from the deck)")
                : (dsb > 0f ? "starboard" : "port");

            g.MakeMap(vx, vy, tris, n);
            g.VX = vx;
            g.VY = vy;
            g.Foot = FindFoot(w, vx, vy, tris, n);
            g.Info = g.W + "x" + g.H + " texels, cloth " + g.SpanX.ToString("F1") + "x" + g.SpanY.ToString("F1") + " m, middle " +
                     g.CenterS.ToString("F2") + "," + g.CenterT.ToString("F2") + ", up along s " + ys.ToString("F1") + " t " + yt.ToString("F1") +
                     ", right along s " + xs.ToString("F1") + " t " + xt.ToString("F1") + ", " + drawn + " texels on the cloth" +
                     (fitted ? "" : " (no fit, 5 m square assumed)");
            return g;
        }

        // The cloth on a grid in X, Y (about 256 cells along its longer side) and, for each cell on it, the distance
        // to the nearest cell off it (two-pass chamfer distance, 1 and sqrt 2).
        private void MakeMap(float[] vx, float[] vy, int[] tris, int n)
        {
            cell = Mathf.Max(SpanX, SpanY) / 256f;
            mapX0 = MinX - 2f * cell;
            mapY0 = MinY - 2f * cell;
            mapW = Mathf.CeilToInt(SpanX / cell) + 4;
            mapH = Mathf.CeilToInt(SpanY / cell) + 4;
            edge = new float[mapW * mapH];
            const float Far = 1e9f;
            if (tris != null)
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                    if (a >= n || b >= n || c >= n) continue;
                    Raster(mapW, mapH,
                        (vx[a] - mapX0) / cell - 0.5f, (vy[a] - mapY0) / cell - 0.5f, (vx[b] - mapX0) / cell - 0.5f, (vy[b] - mapY0) / cell - 0.5f,
                        (vx[c] - mapX0) / cell - 0.5f, (vy[c] - mapY0) / cell - 0.5f, 0.5f,
                        (i, l0, l1, l2) => edge[i] = Far);
                }
            const float D = 1.41421356f;
            for (int y = 0; y < mapH; y++)
                for (int x = 0; x < mapW; x++)
                {
                    int i = y * mapW + x;
                    if (edge[i] == 0f) continue;
                    float v = edge[i];
                    if (x > 0) v = Mathf.Min(v, edge[i - 1] + 1f);
                    if (y > 0)
                    {
                        v = Mathf.Min(v, edge[i - mapW] + 1f);
                        if (x > 0) v = Mathf.Min(v, edge[i - mapW - 1] + D);
                        if (x < mapW - 1) v = Mathf.Min(v, edge[i - mapW + 1] + D);
                    }
                    edge[i] = v;
                }
            for (int y = mapH - 1; y >= 0; y--)
                for (int x = mapW - 1; x >= 0; x--)
                {
                    int i = y * mapW + x;
                    if (edge[i] == 0f) continue;
                    float v = edge[i];
                    if (x < mapW - 1) v = Mathf.Min(v, edge[i + 1] + 1f);
                    if (y < mapH - 1)
                    {
                        v = Mathf.Min(v, edge[i + mapW] + 1f);
                        if (x < mapW - 1) v = Mathf.Min(v, edge[i + mapW + 1] + D);
                        if (x > 0) v = Mathf.Min(v, edge[i + mapW - 1] + D);
                    }
                    edge[i] = v;
                }
            // cells to metres, measured from the cell's edge rather than its centre
            for (int i = 0; i < edge.Length; i++)
                edge[i] = edge[i] > 0f ? (edge[i] - 0.5f) * cell : -0.5f * cell;
        }

        // The foot: the cloth's outer edges that face down (welded by position: UV seams split vertices), their
        // directions summed by length. A gaff's foot runs along its boom, which need not be level (Кэп).
        private static Vector2 FindFoot(Vector3[] w, float[] vx, float[] vy, int[] tris, int n)
        {
            if (tris == null || n < 3) return Vector2.right;
            var weld = new System.Collections.Generic.Dictionary<Vector3Int, int>();
            var id = new int[n];
            for (int i = 0; i < n; i++)
            {
                var k = new Vector3Int(Mathf.RoundToInt(w[i].x * 1000f), Mathf.RoundToInt(w[i].y * 1000f), Mathf.RoundToInt(w[i].z * 1000f));
                if (!weld.TryGetValue(k, out id[i])) weld[k] = id[i] = i;
            }
            // edge (welded ends) -> how many triangles use it, and one of them: its ends and third corner
            var edges = new System.Collections.Generic.Dictionary<long, int[]>();
            for (int k = 0; k + 2 < tris.Length; k += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[k + e], b = tris[k + (e + 1) % 3], c = tris[k + (e + 2) % 3];
                    if (a >= n || b >= n || c >= n) continue;
                    int ia = id[a], ib = id[b];
                    if (ia == ib) continue;
                    long key = ia < ib ? (long)ia << 32 | (uint)ib : (long)ib << 32 | (uint)ia;
                    if (edges.TryGetValue(key, out int[] rec)) rec[3]++;
                    else edges[key] = new[] { a, b, c, 1 };
                }
            Vector2 sum = Vector2.zero;
            foreach (int[] rec in edges.Values)
            {
                if (rec[3] != 1) continue;
                var pa = new Vector2(vx[rec[0]], vy[rec[0]]);
                var d = new Vector2(vx[rec[1]], vy[rec[1]]) - pa;
                var pc = new Vector2(vx[rec[2]], vy[rec[2]]);
                var outward = new Vector2(d.y, -d.x).normalized;
                if (Vector2.Dot(outward, pc - pa) > 0f) outward = -outward;
                if (outward.y > -0.7f) continue; // not facing down
                if (d.x < 0f) d = -d;
                sum += d;
            }
            if (sum.magnitude < 1e-4f) return Vector2.right;
            sum.Normalize();
            // a foot steeper than 40 degrees is not a boom
            return sum.x < Mathf.Cos(40f * Mathf.Deg2Rad) ? Vector2.right : sum;
        }

        // Mathf.NextPowerOfTwo is native: this one runs outside the game too (tests)
        private static int Pow2(int v)
        {
            int p = 1;
            while (p < v && p < (1 << 20)) p <<= 1;
            return p;
        }

        /// <summary>Calls <paramref name="put"/> with the barycentric weights for each grid point inside the triangle (pad: extra texels around it).</summary>
        private static int Raster(int gw, int gh, float x0, float y0, float x1, float y1, float x2, float y2, float pad,
                                  Action<int, float, float, float> put)
        {
            float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Mathf.Abs(area) < 1e-9f) return 0;
            int xa = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2)) - pad));
            int xb = Mathf.Min(gw - 1, Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2)) + pad));
            int ya = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2)) - pad));
            int yb = Mathf.Min(gh - 1, Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2)) + pad));
            // a texel counts if its centre is within half a texel of the triangle: no gaps along shared edges
            float inv = 1f / area;
            float e0 = pad * Mathf.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) * Mathf.Abs(inv);
            float e1 = pad * Mathf.Sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2)) * Mathf.Abs(inv);
            float e2 = pad * Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) * Mathf.Abs(inv);
            int count = 0;
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    float l0 = ((x1 - x) * (y2 - y) - (x2 - x) * (y1 - y)) * inv;
                    float l1 = ((x2 - x) * (y0 - y) - (x0 - x) * (y2 - y)) * inv;
                    float l2 = 1f - l0 - l1;
                    if (l0 < -e0 || l1 < -e1 || l2 < -e2) continue;
                    put(y * gw + x, l0, l1, l2);
                    count++;
                }
            return count;
        }
    }
}
