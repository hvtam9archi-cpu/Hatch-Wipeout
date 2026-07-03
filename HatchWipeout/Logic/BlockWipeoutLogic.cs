using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HatchWipeout.Logic
{
    public static class BlockWipeoutLogic
    {
        public static int Execute(Database db, Transaction tr, ObjectId[] selectedBlockRefIds)
        {
            var uniqueBlockRecordIds = new HashSet<ObjectId>();

            foreach (ObjectId blockRefId in selectedBlockRefIds)
            {
                var blockRef = tr.GetObject(blockRefId, OpenMode.ForRead) as BlockReference;
                if (blockRef == null) continue;

                ObjectId blockRecordId = GetEffectiveBlockTableRecordId(blockRef);
                uniqueBlockRecordIds.Add(blockRecordId);
            }

            int processedCount = 0;

            foreach (ObjectId blockRecordId in uniqueBlockRecordIds)
            {
                var blockRecord = tr.GetObject(blockRecordId, OpenMode.ForWrite) as BlockTableRecord;
                if (blockRecord == null) continue;

                bool success = ProcessBlockRecord(blockRecord, tr, db);
                if (success) processedCount++;
            }

            return processedCount;
        }

        /// <summary>
        /// Xử lý chính cho một BlockTableRecord:
        /// 1. Thu thập tất cả curves (giữ nguyên closed curves, explode open polylines)
        /// 2. Tạo Region riêng biệt cho closed curves
        /// 3. Shatter open curves + tạo Region từ soup
        /// 4. Union tất cả Regions
        /// 5. Tạo Wipeout từ Region cuối cùng
        /// </summary>
        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr, Database db)
        {
            // Thu thập curves, phân loại thành closed và open
            var closedCurves = new List<Curve>();
            var openCurves = new List<Curve>();
            CollectCurvesFromBlock(blockRecord, tr, Matrix3d.Identity, closedCurves, openCurves);

            if (closedCurves.Count == 0 && openCurves.Count == 0) return false;

            var allRegions = new List<Region>();

            // === PHẦN 1: Tạo Region từ mỗi closed curve riêng biệt ===
            foreach (var closedCurve in closedCurves)
            {
                var regions = CreateRegionsFromSingleCurve(closedCurve);
                allRegions.AddRange(regions);
            }

            // === PHẦN 2: Shatter open curves tại giao điểm + tạo Region ===
            if (openCurves.Count > 0)
            {
                var allCurvesForShatter = new List<Curve>();
                allCurvesForShatter.AddRange(openCurves);
                foreach (var cc in closedCurves)
                {
                    allCurvesForShatter.Add(cc.Clone() as Curve);
                }

                List<Curve> shatteredSoup = ShatterCurvesAtIntersections(allCurvesForShatter);
                var soupRegions = CreateRegionsFromSoup(shatteredSoup);
                allRegions.AddRange(soupRegions);

                foreach (var c in shatteredSoup) if (!c.IsDisposed) c.Dispose();
            }

            if (allRegions.Count == 0)
            {
                foreach (var c in closedCurves) if (!c.IsDisposed) c.Dispose();
                foreach (var c in openCurves) if (!c.IsDisposed) c.Dispose();
                return false;
            }

            // === PHẦN 3: Union tất cả Regions ===
            Region finalRegion = UnionRegions(allRegions);

            bool result = false;
            if (finalRegion != null)
            {
                // === PHẦN 4: Tạo Wipeout từ Region ===
                CreateWipeoutFromRegion(blockRecord, tr, db, finalRegion);
                result = true;
                finalRegion.Dispose();
            }

            // Cleanup
            foreach (var c in closedCurves) if (!c.IsDisposed) c.Dispose();
            foreach (var c in openCurves) if (!c.IsDisposed) c.Dispose();

            return result;
        }

        private static ObjectId GetEffectiveBlockTableRecordId(BlockReference blockRef)
        {
            if (blockRef.IsDynamicBlock)
                return blockRef.DynamicBlockTableRecord;
            return blockRef.BlockTableRecord;
        }

        // =====================================================================
        // 1. THU THẬP CURVES – PHÂN LOẠI CLOSED / OPEN
        // =====================================================================

        private static void CollectCurvesFromBlock(
            BlockTableRecord btr, Transaction tr, Matrix3d parentTransform,
            List<Curve> closedCurves, List<Curve> openCurves)
        {
            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !ent.Visible) continue;

                Entity clonedEnt = ent.Clone() as Entity;
                clonedEnt.TransformBy(parentTransform);

                if (clonedEnt is BlockReference blockRef)
                {
                    try
                    {
                        ObjectId nestedRecordId = GetEffectiveBlockTableRecordId(blockRef);
                        var nestedRecord = tr.GetObject(nestedRecordId, OpenMode.ForRead) as BlockTableRecord;
                        if (nestedRecord != null)
                        {
                            CollectCurvesFromBlock(nestedRecord, tr,
                                parentTransform * blockRef.BlockTransform, closedCurves, openCurves);
                        }
                    }
                    catch { }
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Circle circle)
                {
                    Curve flat = FlattenCircle(circle);
                    if (flat != null) closedCurves.Add(flat);
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Ellipse ellipse)
                {
                    Curve flat = FlattenEllipse(ellipse);
                    if (flat != null) closedCurves.Add(flat);
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Polyline polyline)
                {
                    Curve flat = FlattenPolyline(polyline);
                    if (flat != null)
                    {
                        if (((Polyline)flat).Closed)
                            closedCurves.Add(flat);
                        else
                            ExplodeAndAddOpen(flat, openCurves);
                    }
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Polyline2d || clonedEnt is Polyline3d)
                {
                    DBObjectCollection exploded = new DBObjectCollection();
                    try { clonedEnt.Explode(exploded); } catch { }
                    foreach (DBObject obj in exploded)
                    {
                        if (obj is Curve c)
                        {
                            Curve flat = FlattenBasicCurve(c);
                            if (flat != null) openCurves.Add(flat);
                            else c.Dispose();
                        }
                        else obj.Dispose();
                    }
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Line || clonedEnt is Arc || clonedEnt is Spline)
                {
                    Curve flat = FlattenBasicCurve(clonedEnt as Curve);
                    if (flat != null) openCurves.Add(flat);
                    else clonedEnt.Dispose();
                }
                else
                {
                    clonedEnt.Dispose();
                }
            }
        }

        private static void ExplodeAndAddOpen(Curve curve, List<Curve> openCurves)
        {
            DBObjectCollection exploded = new DBObjectCollection();
            try { curve.Explode(exploded); } catch { openCurves.Add(curve); return; }

            foreach (DBObject obj in exploded)
            {
                if (obj is Curve c)
                {
                    Curve flat = FlattenBasicCurve(c);
                    if (flat != null) openCurves.Add(flat);
                    else c.Dispose();
                }
                else obj.Dispose();
            }
            curve.Dispose();
        }

        // =====================================================================
        // FLATTEN HELPERS
        // =====================================================================

        private static Curve FlattenCircle(Circle circle)
        {
            try
            {
                var c = circle.Clone() as Circle;
                c.Center = new Point3d(c.Center.X, c.Center.Y, 0);
                c.Normal = Vector3d.ZAxis;
                return c;
            }
            catch { return null; }
        }

        private static Curve FlattenEllipse(Ellipse ellipse)
        {
            try
            {
                var e = ellipse.Clone() as Ellipse;
                e.Set(
                    new Point3d(e.Center.X, e.Center.Y, 0),
                    Vector3d.ZAxis,
                    new Vector3d(e.MajorAxis.X, e.MajorAxis.Y, 0),
                    e.RadiusRatio,
                    e.StartAngle,
                    e.EndAngle
                );
                return e;
            }
            catch { return null; }
        }

        private static Curve FlattenPolyline(Polyline polyline)
        {
            try
            {
                var p = polyline.Clone() as Polyline;
                p.Elevation = 0;
                p.Normal = Vector3d.ZAxis;
                return p;
            }
            catch { return null; }
        }

        private static Curve FlattenBasicCurve(Curve cv)
        {
            try
            {
                if (cv is Line ln)
                {
                    if (ln.Length < 1e-4) return null;
                    var l = ln.Clone() as Line;
                    l.StartPoint = new Point3d(l.StartPoint.X, l.StartPoint.Y, 0);
                    l.EndPoint = new Point3d(l.EndPoint.X, l.EndPoint.Y, 0);
                    return l;
                }
                else if (cv is Arc arc)
                {
                    var a = arc.Clone() as Arc;
                    a.Center = new Point3d(a.Center.X, a.Center.Y, 0);
                    a.Normal = Vector3d.ZAxis;
                    return a;
                }
                else if (cv is Spline spline && spline.IsPlanar)
                {
                    return spline.Clone() as Curve;
                }
                return null;
            }
            catch { return null; }
        }

        // =====================================================================
        // 2. REGION TỪ SINGLE CLOSED CURVE
        // =====================================================================

        private static List<Region> CreateRegionsFromSingleCurve(Curve closedCurve)
        {
            var regions = new List<Region>();
            var col = new DBObjectCollection { closedCurve };
            try
            {
                DBObjectCollection res = Region.CreateFromCurves(col);
                foreach (DBObject obj in res)
                {
                    if (obj is Region r) regions.Add(r);
                    else obj.Dispose();
                }
            }
            catch { }
            return regions;
        }

        // =====================================================================
        // 3. SHATTER (CẮT GIAO ĐIỂM)
        // =====================================================================

        private static List<Curve> ShatterCurvesAtIntersections(List<Curve> inputCurves)
        {
            List<Curve> workingSet = new List<Curve>();
            foreach (var c in inputCurves) workingSet.Add(c.Clone() as Curve);

            Dictionary<Curve, List<double>> splitMap = new Dictionary<Curve, List<double>>();
            foreach (var c in workingSet) splitMap[c] = new List<double>();

            for (int i = 0; i < workingSet.Count; i++)
            {
                for (int j = i + 1; j < workingSet.Count; j++)
                {
                    Curve c1 = workingSet[i];
                    Curve c2 = workingSet[j];

                    Point3dCollection pts = new Point3dCollection();
                    try { c1.IntersectWith(c2, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero); }
                    catch { continue; }

                    foreach (Point3d pt in pts)
                    {
                        try { splitMap[c1].Add(c1.GetParameterAtPoint(pt)); } catch { }
                        try { splitMap[c2].Add(c2.GetParameterAtPoint(pt)); } catch { }
                    }
                }
            }

            List<Curve> result = new List<Curve>();
            foreach (var kvp in splitMap)
            {
                Curve c = kvp.Key;
                List<double> paramsList = kvp.Value.Distinct().OrderBy(x => x).ToList();
                double start = c.StartParam;
                double end = c.EndParam;

                paramsList.RemoveAll(p => Math.Abs(p - start) < 1e-5 || Math.Abs(p - end) < 1e-5);

                if (paramsList.Count > 0)
                {
                    try
                    {
                        DoubleCollection doubles = new DoubleCollection(paramsList.ToArray());
                        DBObjectCollection pieces = c.GetSplitCurves(doubles);
                        foreach (DBObject obj in pieces)
                        {
                            if (obj is Curve piece) result.Add(piece);
                            else obj.Dispose();
                        }
                    }
                    catch { result.Add(c.Clone() as Curve); }
                }
                else result.Add(c.Clone() as Curve);
            }

            foreach (var c in workingSet) c.Dispose();
            return result;
        }

        // =====================================================================
        // 4. REGION OPERATIONS
        // =====================================================================

        private static List<Region> CreateRegionsFromSoup(List<Curve> soup)
        {
            List<Region> regions = new List<Region>();
            if (soup.Count == 0) return regions;

            DBObjectCollection col = new DBObjectCollection();
            foreach (var c in soup) col.Add(c);

            try
            {
                DBObjectCollection res = Region.CreateFromCurves(col);
                foreach (DBObject obj in res)
                {
                    if (obj is Region r) regions.Add(r);
                    else obj.Dispose();
                }
            }
            catch { }

            return regions;
        }

        private static Region UnionRegions(List<Region> regions)
        {
            if (regions.Count == 0) return null;
            Region main = regions[0];
            for (int i = 1; i < regions.Count; i++)
            {
                try { main.BooleanOperation(BooleanOperationType.BoolUnite, regions[i]); }
                catch { }
                regions[i].Dispose();
            }
            return main;
        }

        // =====================================================================
        // 5. TẠO WIPEOUT TỪ REGION
        // =====================================================================

        /// <summary>
        /// Tạo Wipeout bên trong Block Definition từ Region.
        /// Wipeout chỉ nhận Polyline (đa giác phẳng), nên ta cần:
        /// 1. Explode Region → lấy boundary curves
        /// 2. Join + facet thành closed Polyline
        /// 3. Lấy các đỉnh tạo Point2dCollection cho Wipeout
        /// </summary>
        private static void CreateWipeoutFromRegion(
            BlockTableRecord blockRecord, Transaction tr, Database db, Region region)
        {
            // Explode Region để lấy boundary curves
            DBObjectCollection boundaryObjs = new DBObjectCollection();
            region.Explode(boundaryObjs);

            var allCurves = new List<Curve>();
            foreach (DBObject obj in boundaryObjs)
            {
                if (obj is Curve c) allCurves.Add(c);
                else obj.Dispose();
            }

            if (allCurves.Count == 0) return;

            // Join curves thành closed polylines (faceted)
            var closedPolylines = JoinCurvesToClosedPolylines(allCurves);
            if (closedPolylines.Count == 0)
            {
                foreach (var c in allCurves) if (!c.IsDisposed) c.Dispose();
                return;
            }

            // Lấy polyline bao ngoài cùng (diện tích lớn nhất) để tạo Wipeout
            var outermost = closedPolylines.OrderByDescending(p => p.Area).First();

            // Lấy danh sách đỉnh từ Polyline
            int vertexCount = outermost.NumberOfVertices;
            // Wipeout cần Point2dCollection: đỉnh đầu tiên phải lặp lại ở cuối để khép kín
            var wipeoutPoints = new Point2dCollection();
            for (int i = 0; i < vertexCount; i++)
            {
                Point2d pt = outermost.GetPoint2dAt(i);
                wipeoutPoints.Add(pt);
            }
            // Khép kín: thêm điểm đầu tiên vào cuối
            wipeoutPoints.Add(outermost.GetPoint2dAt(0));

            // Tạo Wipeout
            var wipeout = new Wipeout();
            wipeout.SetDatabaseDefaults(db);
            wipeout.SetFrom(wipeoutPoints, Vector3d.ZAxis);

            ObjectId wipeoutId = blockRecord.AppendEntity(wipeout);
            tr.AddNewlyCreatedDBObject(wipeout, true);

            // Đặt DrawOrder xuống dưới cùng
            SetDrawOrderToBottom(blockRecord, tr, wipeoutId);

            // Cleanup
            foreach (var pl in closedPolylines) if (!pl.IsDisposed) pl.Dispose();
            foreach (var c in allCurves) if (!c.IsDisposed) c.Dispose();
        }

        // =====================================================================
        // JOIN CURVES TO POLYLINES
        // =====================================================================

        private static List<Polyline> JoinCurvesToClosedPolylines(List<Curve> curves)
        {
            List<Polyline> results = new List<Polyline>();

            List<Polyline> facetedPool = new List<Polyline>();
            foreach (var c in curves)
            {
                Polyline faceted = FacetCurveToPolyline(c, 64);
                if (faceted != null && faceted.NumberOfVertices > 1)
                {
                    facetedPool.Add(faceted);
                }
            }

            while (facetedPool.Count > 0)
            {
                Polyline pl = facetedPool[0];
                facetedPool.RemoveAt(0);

                bool extended = true;
                while (extended)
                {
                    extended = false;
                    for (int i = facetedPool.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            pl.JoinEntity(facetedPool[i]);
                            facetedPool[i].Dispose();
                            facetedPool.RemoveAt(i);
                            extended = true;
                        }
                        catch { }
                    }
                }
                if (!pl.Closed) pl.Closed = true;
                if (pl.Area > 1e-4) results.Add(pl);
                else pl.Dispose();
            }
            return results;
        }

        private static Polyline FacetCurveToPolyline(Curve cv, int minSegments)
        {
            Polyline pl = new Polyline();
            pl.Elevation = 0;

            if (cv is Line ln)
            {
                pl.AddVertexAt(0, new Point2d(ln.StartPoint.X, ln.StartPoint.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(ln.EndPoint.X, ln.EndPoint.Y), 0, 0, 0);
                return pl;
            }

            try
            {
                double startParam = cv.StartParam;
                double endParam = cv.EndParam;

                if (cv.GetDistanceAtParameter(endParam) < 1e-4) return null;

                for (int i = 0; i <= minSegments; i++)
                {
                    double t = (double)i / minSegments;
                    double param = startParam + t * (endParam - startParam);
                    if (i == minSegments) param = endParam;

                    Point3d pt = cv.GetPointAtParameter(param);
                    pl.AddVertexAt(i, new Point2d(pt.X, pt.Y), 0, 0, 0);
                }
                return pl;
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // DRAW ORDER
        // =====================================================================

        private static void SetDrawOrderToBottom(BlockTableRecord blockRecord, Transaction tr, ObjectId entityId)
        {
            var drawOrderTable = tr.GetObject(blockRecord.DrawOrderTableId, OpenMode.ForWrite) as DrawOrderTable;
            if (drawOrderTable == null) return;

            var entityIds = new ObjectIdCollection { entityId };
            drawOrderTable.MoveToBottom(entityIds);
        }
    }
}
