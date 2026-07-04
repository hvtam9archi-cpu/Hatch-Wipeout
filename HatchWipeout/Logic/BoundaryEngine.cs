using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace HatchWipeout.Logic
{
    /// <summary>
    /// Đoạn thẳng 2D thuần túy, đại diện cho một cạnh hình học đã rời rạc hóa.
    /// </summary>
    public struct Seg2d
    {
        public readonly double AX, AY, BX, BY;

        public Seg2d(double ax, double ay, double bx, double by)
        {
            AX = ax; AY = ay; BX = bx; BY = by;
        }

        public Seg2d(Point2d a, Point2d b)
        {
            AX = a.X; AY = a.Y; BX = b.X; BY = b.Y;
        }

        public double LengthSquared
        {
            get
            {
                double dx = BX - AX, dy = BY - AY;
                return dx * dx + dy * dy;
            }
        }
    }

    /// <summary>
    /// Engine thuật toán Boundary Extraction:
    ///   GĐ1. Discretization – Chuyển tất cả Curve thành đoạn thẳng (60 phần/đường cong)
    ///   GĐ2. Fragmentation  – Tìm giao điểm và bẻ gãy (Spatial Grid tối ưu)
    ///   GĐ3. Boundary Walk  – Truy vết biên ngoài cùng (planar face traversal)
    /// </summary>
    public static class BoundaryEngine
    {
        private const double Tol = 1e-6;
        private const double TolSq = Tol * Tol;
        private const int ArcSegments = 60;

        // ════════════════════════════════════════════════════════════════
        //  GIAI ĐOẠN 1: DISCRETIZATION
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Chuyển toàn bộ danh sách Curve (đã flatten về mặt phẳng XY) thành các đoạn thẳng.
        /// Mỗi đường cong được chia thành 60 đoạn thẳng liên tiếp.
        /// </summary>
        public static List<Seg2d> DiscretizeCurves(List<Curve> curves)
        {
            var result = new List<Seg2d>(curves.Count * ArcSegments);
            foreach (var c in curves)
            {
                if (c == null || c.IsDisposed) continue;
                try { DiscretizeSingle(c, result); }
                catch { /* Bỏ qua curve lỗi */ }
            }
            return result;
        }

        private static void DiscretizeSingle(Curve curve, List<Seg2d> output)
        {
            if (curve is Line ln)
                DiscretizeLine(ln, output);
            else if (curve is Polyline pl)
                DiscretizePolyline(pl, output);
            else if (curve is Circle ci)
                DiscretizeCircle(ci, output);
            else if (curve is Arc ar)
                DiscretizeArcEntity(ar, output);
            else if (curve is Ellipse el)
                DiscretizeEllipse(el, output);
            else
                DiscretizeGeneric(curve, output);
        }

        private static void DiscretizeLine(Line ln, List<Seg2d> output)
        {
            double dx = ln.EndPoint.X - ln.StartPoint.X;
            double dy = ln.EndPoint.Y - ln.StartPoint.Y;
            if (dx * dx + dy * dy < TolSq) return;
            output.Add(new Seg2d(ln.StartPoint.X, ln.StartPoint.Y,
                                  ln.EndPoint.X, ln.EndPoint.Y));
        }

        private static void DiscretizePolyline(Polyline pl, List<Seg2d> output)
        {
            int n = pl.NumberOfVertices;
            if (n < 2) return;
            int limit = pl.Closed ? n : n - 1;

            for (int i = 0; i < limit; i++)
            {
                int j = (i + 1) % n;
                var a = pl.GetPoint2dAt(i);
                var b = pl.GetPoint2dAt(j);
                double bulge = pl.GetBulgeAt(i);

                if (Math.Abs(bulge) < Tol)
                {
                    double ddx = b.X - a.X, ddy = b.Y - a.Y;
                    if (ddx * ddx + ddy * ddy >= TolSq)
                        output.Add(new Seg2d(a, b));
                }
                else
                {
                    SubdivideArc(a.X, a.Y, b.X, b.Y, bulge, output);
                }
            }
        }

        /// <summary>
        /// Chia cung tròn (xác định bởi 2 endpoint + bulge) thành 60 đoạn thẳng.
        /// Sử dụng công thức lượng giác thuần túy.
        /// </summary>
        private static void SubdivideArc(double x1, double y1, double x2, double y2,
                                          double bulge, List<Seg2d> output)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < Tol) return;

            double theta = 4.0 * Math.Atan(Math.Abs(bulge));
            double halfSin = Math.Sin(theta * 0.5);
            if (Math.Abs(halfSin) < Tol) return;

            double r = chord / (2.0 * halfSin);
            double mx = (x1 + x2) * 0.5, my = (y1 + y2) * 0.5;
            double nx = -dy / chord, ny = dx / chord;
            double d = r * Math.Cos(theta * 0.5);

            double cx, cy;
            if (bulge > 0) { cx = mx + d * nx; cy = my + d * ny; }
            else            { cx = mx - d * nx; cy = my - d * ny; }

            double sa = Math.Atan2(y1 - cy, x1 - cx);
            double ea = Math.Atan2(y2 - cy, x2 - cx);
            double sweep = ea - sa;
            if (bulge > 0) { if (sweep <= Tol) sweep += Math.PI * 2; }
            else            { if (sweep >= -Tol) sweep -= Math.PI * 2; }

            double px = x1, py = y1;
            for (int k = 1; k <= ArcSegments; k++)
            {
                double a = sa + sweep * k / ArcSegments;
                double qx = cx + r * Math.Cos(a), qy = cy + r * Math.Sin(a);
                output.Add(new Seg2d(px, py, qx, qy));
                px = qx; py = qy;
            }
        }

        private static void DiscretizeCircle(Circle ci, List<Seg2d> output)
        {
            double cx = ci.Center.X, cy = ci.Center.Y, r = ci.Radius;
            double px = cx + r, py = cy;
            for (int k = 1; k <= ArcSegments; k++)
            {
                double a = Math.PI * 2 * k / ArcSegments;
                double qx = cx + r * Math.Cos(a), qy = cy + r * Math.Sin(a);
                output.Add(new Seg2d(px, py, qx, qy));
                px = qx; py = qy;
            }
        }

        private static void DiscretizeArcEntity(Arc arc, List<Seg2d> output)
        {
            double cx = arc.Center.X, cy = arc.Center.Y, r = arc.Radius;
            double sa = arc.StartAngle, ea = arc.EndAngle;
            double sweep = ea - sa;
            if (sweep <= 0) sweep += Math.PI * 2;

            double px = cx + r * Math.Cos(sa), py = cy + r * Math.Sin(sa);
            for (int k = 1; k <= ArcSegments; k++)
            {
                double a = sa + sweep * k / ArcSegments;
                double qx = cx + r * Math.Cos(a), qy = cy + r * Math.Sin(a);
                output.Add(new Seg2d(px, py, qx, qy));
                px = qx; py = qy;
            }
        }

        private static void DiscretizeEllipse(Ellipse el, List<Seg2d> output)
        {
            double ccx = el.Center.X, ccy = el.Center.Y;
            var maj = el.MajorAxis;
            double semiA = maj.Length;
            if (semiA < Tol) return;
            double semiB = semiA * el.RadiusRatio;

            // Trục chính (unit)
            double ux = maj.X / semiA, uy = maj.Y / semiA;
            // Trục phụ vuông góc
            double vx = -uy, vy = ux;

            double sp = el.StartParam, ep = el.EndParam;
            double range = ep - sp;
            if (Math.Abs(range) < Tol) return;

            double px = 0, py = 0;
            for (int k = 0; k <= ArcSegments; k++)
            {
                double t = sp + range * k / ArcSegments;
                double lx = semiA * Math.Cos(t), ly = semiB * Math.Sin(t);
                double qx = ccx + lx * ux + ly * vx;
                double qy = ccy + lx * uy + ly * vy;
                if (k > 0) output.Add(new Seg2d(px, py, qx, qy));
                px = qx; py = qy;
            }
        }

        /// <summary>
        /// Fallback cho Spline và các loại Curve khác: dùng GetPointAtParameter chia đều 60 phần.
        /// </summary>
        private static void DiscretizeGeneric(Curve curve, List<Seg2d> output)
        {
            try
            {
                double sp = curve.StartParam, ep = curve.EndParam;
                double range = ep - sp;
                if (Math.Abs(range) < Tol) return;

                double px = 0, py = 0;
                for (int k = 0; k <= ArcSegments; k++)
                {
                    double param = sp + range * k / ArcSegments;
                    var pt = curve.GetPointAtParameter(param);
                    if (k > 0) output.Add(new Seg2d(px, py, pt.X, pt.Y));
                    px = pt.X; py = pt.Y;
                }
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════
        //  GIAI ĐOẠN 2: FRAGMENTATION (Spatial Grid tối ưu)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tìm giao điểm giữa mọi cặp đoạn thẳng (cross + T-intersection) và bẻ gãy tại đó.
        /// Sử dụng Spatial Grid cho hiệu năng gần O(n√n).
        /// </summary>
        public static List<Seg2d> FragmentAtIntersections(List<Seg2d> segs)
        {
            int n = segs.Count;
            if (n < 2) return new List<Seg2d>(segs);

            var grid = new SpatialGrid(segs);
            var splits = new List<double>[n];
            for (int i = 0; i < n; i++) splits[i] = new List<double>();

            var checkedPairs = new HashSet<long>();

            for (int i = 0; i < n; i++)
            {
                foreach (int j in grid.GetCandidates(i))
                {
                    if (j <= i) continue;
                    long key = ((long)i << 32) | (uint)j;
                    if (!checkedPairs.Add(key)) continue;

                    // Pass 1: Cross intersection (cả t và s đều nằm trong segment)
                    double ti, tj;
                    if (CrossIntersect(segs[i], segs[j], out ti, out tj))
                    {
                        splits[i].Add(ti);
                        splits[j].Add(tj);
                    }

                    // Pass 2: T-intersection (endpoint của segment này nằm trên interior segment kia)
                    PointOnInterior(segs[j].AX, segs[j].AY, segs[i], splits[i]);
                    PointOnInterior(segs[j].BX, segs[j].BY, segs[i], splits[i]);
                    PointOnInterior(segs[i].AX, segs[i].AY, segs[j], splits[j]);
                    PointOnInterior(segs[i].BX, segs[i].BY, segs[j], splits[j]);
                }
            }

            // Chặt từng segment tại các điểm giao
            var result = new List<Seg2d>(n * 2);
            for (int i = 0; i < n; i++)
            {
                if (splits[i].Count == 0) { result.Add(segs[i]); continue; }
                splits[i].Sort();
                SplitSegment(segs[i], splits[i], result);
            }
            return result;
        }

        /// <summary>Tìm giao điểm nội bộ (cả t và s nằm trong (0,1)).</summary>
        private static bool CrossIntersect(Seg2d s1, Seg2d s2, out double t, out double s)
        {
            t = s = 0;
            double d1x = s1.BX - s1.AX, d1y = s1.BY - s1.AY;
            double d2x = s2.BX - s2.AX, d2y = s2.BY - s2.AY;
            double denom = d1x * d2y - d1y * d2x;

            double lenProd = Math.Sqrt((d1x * d1x + d1y * d1y) * (d2x * d2x + d2y * d2y));
            if (Math.Abs(denom) < Tol * lenProd) return false;

            double ox = s2.AX - s1.AX, oy = s2.AY - s1.AY;
            t = (ox * d2y - oy * d2x) / denom;
            s = (ox * d1y - oy * d1x) / denom;
            return t > Tol && t < 1 - Tol && s > Tol && s < 1 - Tol;
        }

        /// <summary>Kiểm tra điểm (px,py) có nằm trên phần nội bộ của segment không → thêm split.</summary>
        private static void PointOnInterior(double px, double py, Seg2d seg, List<double> splits)
        {
            double dx = seg.BX - seg.AX, dy = seg.BY - seg.AY;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < TolSq) return;

            double t = ((px - seg.AX) * dx + (py - seg.AY) * dy) / lenSq;
            if (t <= Tol || t >= 1.0 - Tol) return;

            double cross = (px - seg.AX) * dy - (py - seg.AY) * dx;
            if (cross * cross > TolSq * lenSq) return;

            splits.Add(t);
        }

        /// <summary>Chặt segment thành nhiều sub-segment tại các giá trị t đã sắp xếp.</summary>
        private static void SplitSegment(Seg2d seg, List<double> ts, List<Seg2d> output)
        {
            var unique = new List<double> { 0.0 };
            foreach (double t in ts)
                if (t - unique[unique.Count - 1] > Tol) unique.Add(t);
            unique.Add(1.0);

            double dx = seg.BX - seg.AX, dy = seg.BY - seg.AY;
            for (int k = 0; k < unique.Count - 1; k++)
            {
                double t0 = unique[k], t1 = unique[k + 1];
                if (t1 - t0 < Tol) continue;
                output.Add(new Seg2d(
                    seg.AX + t0 * dx, seg.AY + t0 * dy,
                    seg.AX + t1 * dx, seg.AY + t1 * dy));
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  GIAI ĐOẠN 3: BOUNDARY EXTRACTION (Outer Face Walking)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tìm tất cả đường biên ngoài cùng từ danh sách đoạn thẳng đã phân mảnh.
        /// Mỗi connected component cho ra một đường biên khép kín (CCW).
        /// Thuật toán: Snap vertex → Dedup → Prune dangling → Sort by angle → Walk outer face (k+1 rule).
        /// </summary>
        public static List<List<Point2d>> FindAllOuterBoundaries(List<Seg2d> segments)
        {
            if (segments.Count == 0) return new List<List<Point2d>>();

            // Bước 1: Snap vertex — gộp các điểm gần nhau
            var vm = new VertexMap(Tol);
            var edges = new List<int[]>();
            foreach (var seg in segments)
            {
                if (seg.LengthSquared < TolSq) continue;
                int vi = vm.GetOrAdd(seg.AX, seg.AY);
                int vj = vm.GetOrAdd(seg.BX, seg.BY);
                if (vi != vj) edges.Add(new[] { vi, vj });
            }

            int vc = vm.Count;
            if (vc < 3 || edges.Count < 3) return new List<List<Point2d>>();

            // Bước 2: Dedup edges
            var edgeSet = new HashSet<long>();
            var uniqueEdges = new List<int[]>();
            foreach (var e in edges)
            {
                int a = Math.Min(e[0], e[1]), b = Math.Max(e[0], e[1]);
                if (edgeSet.Add(((long)a << 32) | (uint)b))
                    uniqueEdges.Add(e);
            }

            // Bước 3: Build adjacency
            var adj = new List<int>[vc];
            for (int i = 0; i < vc; i++) adj[i] = new List<int>();
            foreach (var e in uniqueEdges)
            {
                adj[e[0]].Add(e[1]);
                adj[e[1]].Add(e[0]);
            }

            // Bước 4: Prune dangling edges (degree=1) lặp đi lặp lại
            var pruneQueue = new Queue<int>();
            for (int v = 0; v < vc; v++)
                if (adj[v].Count == 1) pruneQueue.Enqueue(v);
            while (pruneQueue.Count > 0)
            {
                int v = pruneQueue.Dequeue();
                if (adj[v].Count != 1) continue;
                int nb = adj[v][0];
                adj[v].Clear();
                adj[nb].Remove(v);
                if (adj[nb].Count == 1) pruneQueue.Enqueue(nb);
            }

            // Bước 5: Sort adjacency by angle (cần cho boundary walking)
            double[] pts = vm.GetAllPoints(); // flat array: [x0,y0, x1,y1, ...]
            for (int v = 0; v < vc; v++)
            {
                if (adj[v].Count < 2) continue;
                double vx = pts[v * 2], vy = pts[v * 2 + 1];
                adj[v].Sort((a, b) =>
                {
                    double angA = Math.Atan2(pts[a * 2 + 1] - vy, pts[a * 2] - vx);
                    double angB = Math.Atan2(pts[b * 2 + 1] - vy, pts[b * 2] - vx);
                    return angA.CompareTo(angB);
                });
            }

            // Bước 6: Tìm connected components (chỉ giữ vertex có degree >= 2)
            var visited = new bool[vc];
            var components = new List<List<int>>();
            for (int v = 0; v < vc; v++)
            {
                if (visited[v] || adj[v].Count < 2) continue;
                var comp = new List<int>();
                var stk = new Stack<int>();
                stk.Push(v); visited[v] = true;
                while (stk.Count > 0)
                {
                    int u = stk.Pop();
                    if (adj[u].Count >= 2) comp.Add(u);
                    foreach (int w in adj[u])
                        if (!visited[w]) { visited[w] = true; stk.Push(w); }
                }
                if (comp.Count >= 3) components.Add(comp);
            }

            // Bước 7: Walk outer boundary cho từng component
            var boundaries = new List<List<Point2d>>();
            foreach (var comp in components)
            {
                // Tìm đỉnh trái nhất (min X, rồi min Y) — chắc chắn nằm trên biên ngoài
                int sv = comp[0];
                double sx = pts[sv * 2], sy = pts[sv * 2 + 1];
                foreach (int v in comp)
                {
                    double vx = pts[v * 2], vy = pts[v * 2 + 1];
                    if (vx < sx - Tol || (Math.Abs(vx - sx) < Tol && vy < sy - Tol))
                    { sv = v; sx = vx; sy = vy; }
                }
                if (adj[sv].Count < 2) continue;

                // Cạnh khởi đầu: góc nhỏ nhất (hướng xuống-phải nhất) từ đỉnh trái nhất
                int firstNext = adj[sv][0];

                var loop = WalkOuterFace(sv, firstNext, adj, pts);
                if (loop == null || loop.Count < 3) continue;

                // Chuyển sang Point2d
                var boundary = new List<Point2d>(loop.Count);
                foreach (int v in loop)
                    boundary.Add(new Point2d(pts[v * 2], pts[v * 2 + 1]));

                // Đảm bảo CCW (signed area > 0)
                double area = SignedArea(boundary);
                if (Math.Abs(area) < Tol) continue;
                if (area < 0) boundary.Reverse();

                boundaries.Add(boundary);
            }

            return boundaries;
        }

        /// <summary>
        /// Truy vết mặt ngoài (outer face) từ đỉnh bắt đầu.
        /// Sử dụng quy tắc (k+1): tại mỗi đỉnh, chọn cạnh kế tiếp theo chiều ngược kim đồng hồ
        /// trong danh sách adjacency đã sort theo góc → luôn đi theo mặt PHẢI (outer face).
        /// </summary>
        private static List<int> WalkOuterFace(int startV, int firstNext,
                                                List<int>[] adj, double[] pts)
        {
            var path = new List<int> { startV };
            int prev = startV, curr = firstNext;

            // Giới hạn an toàn: tổng số cạnh * 2
            int totalEdges = 0;
            for (int i = 0; i < adj.Length; i++) totalEdges += adj[i].Count;
            int maxSteps = Math.Max(totalEdges * 2, 200);

            for (int step = 0; step < maxSteps; step++)
            {
                path.Add(curr);

                // Đã khép kín vòng?
                if (curr == startV)
                {
                    path.RemoveAt(path.Count - 1);
                    return path;
                }

                var nb = adj[curr];
                if (nb.Count < 2) return null; // Dead-end (không nên xảy ra sau pruning)

                int k = nb.IndexOf(prev);
                if (k < 0) return null; // Lỗi đồ thị

                // Quy tắc (k+1): cạnh kế tiếp trong sorted list = mặt phải = outer face
                int next = nb[(k + 1) % nb.Count];
                prev = curr;
                curr = next;
            }
            return null; // Vượt quá giới hạn bước
        }

        /// <summary>Tính diện tích có dấu (Shoelace). Dương = CCW, Âm = CW.</summary>
        private static double SignedArea(List<Point2d> poly)
        {
            double sum = 0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                sum += poly[i].X * poly[j].Y - poly[j].X * poly[i].Y;
            }
            return sum * 0.5;
        }

        // ════════════════════════════════════════════════════════════════
        //  HELPER CLASSES
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Bản đồ vertex: gộp các điểm gần nhau (trong tolerance) thành cùng một index.
        /// Sử dụng spatial hash grid cho tra cứu O(1) trung bình.
        /// </summary>
        private class VertexMap
        {
            private readonly double tolSq, cell;
            private readonly Dictionary<long, List<int>> grid;
            private readonly List<double> coords; // flat: [x0,y0, x1,y1, ...]

            public VertexMap(double tolerance)
            {
                tolSq = tolerance * tolerance;
                cell = tolerance * 3;
                grid = new Dictionary<long, List<int>>();
                coords = new List<double>();
            }

            public int Count => coords.Count / 2;

            public int GetOrAdd(double x, double y)
            {
                int cx = (int)Math.Floor(x / cell);
                int cy = (int)Math.Floor(y / cell);

                // Kiểm tra 9 ô lân cận
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        long key = CellKey(cx + dx, cy + dy);
                        if (grid.TryGetValue(key, out var list))
                            foreach (int idx in list)
                            {
                                double ex = coords[idx * 2] - x;
                                double ey = coords[idx * 2 + 1] - y;
                                if (ex * ex + ey * ey < tolSq) return idx;
                            }
                    }

                // Thêm vertex mới
                int ni = coords.Count / 2;
                coords.Add(x); coords.Add(y);
                long ck = CellKey(cx, cy);
                if (!grid.TryGetValue(ck, out var cl)) { cl = new List<int>(); grid[ck] = cl; }
                cl.Add(ni);
                return ni;
            }

            /// <summary>Trả về mảng phẳng [x0,y0, x1,y1, ...]. Truy cập: pts[i*2]=X, pts[i*2+1]=Y.</summary>
            public double[] GetAllPoints() => coords.ToArray();

            private static long CellKey(int cx, int cy) => ((long)cx << 32) | (uint)cy;
        }

        /// <summary>
        /// Spatial Grid tăng tốc tìm giao điểm — giảm từ O(n²) xuống gần O(n√n).
        /// Chia không gian thành lưới ô vuông, chỉ kiểm tra các cặp segment trong cùng ô.
        /// </summary>
        private class SpatialGrid
        {
            private readonly Dictionary<long, List<int>> cells;
            private readonly double cellSize, ox, oy;
            private readonly List<Seg2d> segs;

            public SpatialGrid(List<Seg2d> segments)
            {
                segs = segments;
                cells = new Dictionary<long, List<int>>();

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var s in segments)
                {
                    double sxMin = Math.Min(s.AX, s.BX), syMin = Math.Min(s.AY, s.BY);
                    double sxMax = Math.Max(s.AX, s.BX), syMax = Math.Max(s.AY, s.BY);
                    if (sxMin < minX) minX = sxMin; if (syMin < minY) minY = syMin;
                    if (sxMax > maxX) maxX = sxMax; if (syMax > maxY) maxY = syMax;
                }

                ox = minX; oy = minY;
                double area = Math.Max((maxX - minX) * (maxY - minY), 1e-10);
                cellSize = Math.Max(Math.Sqrt(area / Math.Max(segments.Count, 1)), Tol * 100);

                for (int i = 0; i < segments.Count; i++) Insert(i, segments[i]);
            }

            private void Insert(int idx, Seg2d s)
            {
                int x0 = CellIdx(Math.Min(s.AX, s.BX) - ox);
                int y0 = CellIdx(Math.Min(s.AY, s.BY) - oy);
                int x1 = CellIdx(Math.Max(s.AX, s.BX) - ox);
                int y1 = CellIdx(Math.Max(s.AY, s.BY) - oy);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cy = y0; cy <= y1; cy++)
                    {
                        long k = CellKey(cx, cy);
                        if (!cells.TryGetValue(k, out var l)) { l = new List<int>(); cells[k] = l; }
                        l.Add(idx);
                    }
            }

            private int CellIdx(double v) => (int)Math.Floor(v / cellSize);

            public IEnumerable<int> GetCandidates(int idx)
            {
                var s = segs[idx];
                int x0 = CellIdx(Math.Min(s.AX, s.BX) - ox);
                int y0 = CellIdx(Math.Min(s.AY, s.BY) - oy);
                int x1 = CellIdx(Math.Max(s.AX, s.BX) - ox);
                int y1 = CellIdx(Math.Max(s.AY, s.BY) - oy);
                var seen = new HashSet<int>();
                for (int cx = x0; cx <= x1; cx++)
                    for (int cy = y0; cy <= y1; cy++)
                    {
                        long k = CellKey(cx, cy);
                        if (cells.TryGetValue(k, out var l))
                            foreach (int j in l)
                                if (j != idx && seen.Add(j)) yield return j;
                    }
            }

            private static long CellKey(int cx, int cy) => ((long)cx << 32) | (uint)cy;
        }
    }
}
