using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HatchWipeout.Logic
{
    public static class BlockHatchLogic
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
        /// 5. Tạo Hatch từ Region cuối cùng
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
            // Circle, Ellipse, closed Polyline → mỗi cái tự tạo được 1 Region
            foreach (var closedCurve in closedCurves)
            {
                var regions = CreateRegionsFromSingleCurve(closedCurve);
                allRegions.AddRange(regions);
            }

            // === PHẦN 2: Shatter open curves tại giao điểm + tạo Region ===
            if (openCurves.Count > 0)
            {
                // Cũng shatter open curves với closed curves (để cắt giao điểm)
                var allCurvesForShatter = new List<Curve>();
                allCurvesForShatter.AddRange(openCurves);
                // Clone closed curves để shatter (giữ nguyên bản gốc đã dùng ở trên)
                foreach (var cc in closedCurves)
                {
                    allCurvesForShatter.Add(cc.Clone() as Curve);
                }

                List<Curve> shatteredSoup = ShatterCurvesAtIntersections(allCurvesForShatter);
                var soupRegions = CreateRegionsFromSoup(shatteredSoup);
                allRegions.AddRange(soupRegions);

                // Cleanup shattered curves
                foreach (var c in shatteredSoup) if (!c.IsDisposed) c.Dispose();
            }

            if (allRegions.Count == 0)
            {
                // Cleanup
                foreach (var c in closedCurves) if (!c.IsDisposed) c.Dispose();
                foreach (var c in openCurves) if (!c.IsDisposed) c.Dispose();
                return false;
            }

            // === PHẦN 3: Union tất cả Regions ===
            Region finalRegion = UnionRegions(allRegions);

            bool result = false;
            if (finalRegion != null)
            {
                // === PHẦN 4: Tạo Hatch trực tiếp từ Region (không cần convert sang Polyline) ===
                CreateSolidHatchFromRegion(blockRecord, tr, db, finalRegion);
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

        /// <summary>
        /// Duyệt tất cả entities trong BlockTableRecord, clone + transform,
        /// phân loại thành closed curves và open curves.
        /// KHÔNG explode Circle, Ellipse, closed Polyline – giữ nguyên để tạo Region chính xác.
        /// </summary>
        private static void CollectCurvesFromBlock(
            BlockTableRecord btr, Transaction tr, Matrix3d parentTransform,
            List<Curve> closedCurves, List<Curve> openCurves)
        {
            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !ent.Visible) continue;

                // Clone + Transform
                Entity clonedEnt = ent.Clone() as Entity;
                clonedEnt.TransformBy(parentTransform);

                if (clonedEnt is BlockReference blockRef)
                {
                    // Đệ quy nested block
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
                    // Circle luôn khép kín – giữ nguyên, flatten Z
                    Curve flat = FlattenCircle(circle);
                    if (flat != null) closedCurves.Add(flat);
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Ellipse ellipse)
                {
                    // Ellipse khép kín – giữ nguyên, flatten
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
                    // Explode Polyline2d/3d thành Line/Arc
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

        /// <summary>
        /// Explode một open Polyline thành Line/Arc rời để tham gia shatter.
        /// </summary>
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

        /// <summary>
        /// Tạo Region từ một đường cong khép kín đơn lẻ (Circle, Ellipse, closed Polyline).
        /// </summary>
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
        // 5. TẠO HATCH TỪ REGION (TRỰC TIẾP, KHÔNG CẦN POLYLINE)
        // =====================================================================

        /// <summary>
        /// Tạo Hatch Solid bên trong Block Definition trực tiếp từ Region.
        /// Explode Region → thêm boundary curves vào block → dùng ObjectId loop → xóa boundary curves.
        /// Cách này giữ chính xác đường cong Arc/Circle mà không cần facet.
        /// </summary>
        private static void CreateSolidHatchFromRegion(
            BlockTableRecord blockRecord, Transaction tr, Database db, Region region)
        {
            // Explode Region để lấy boundary curves
            DBObjectCollection boundaryObjs = new DBObjectCollection();
            region.Explode(boundaryObjs);

            // Phân nhóm boundary curves thành các loop khép kín
            var allCurves = new List<Curve>();
            foreach (DBObject obj in boundaryObjs)
            {
                if (obj is Curve c) allCurves.Add(c);
                else obj.Dispose();
            }

            if (allCurves.Count == 0) return;

            // Thêm tất cả boundary curves vào block (tạm thời, sẽ xóa sau)
            var tempBoundaryIds = new List<ObjectId>();
            foreach (var curve in allCurves)
            {
                ObjectId curveId = blockRecord.AppendEntity(curve);
                tr.AddNewlyCreatedDBObject(curve, true);
                tempBoundaryIds.Add(curveId);
            }

            // Tạo Hatch
            var hatch = new Hatch();
            hatch.SetDatabaseDefaults(db);
            hatch.PatternScale = 1.0;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.HatchStyle = HatchStyle.Normal;
            hatch.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256); // ByLayer

            ObjectId hatchId = blockRecord.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            // Thêm tất cả boundary curves như một Outermost loop
            var loopIds = new ObjectIdCollection();
            foreach (var curveId in tempBoundaryIds)
            {
                loopIds.Add(curveId);
            }

            try
            {
                hatch.AppendLoop(HatchLoopTypes.External, loopIds);
                hatch.EvaluateHatch(true);
            }
            catch
            {
                // Fallback: nếu không gộp được thành 1 loop, thử từng curve riêng
                try
                {
                    // Xóa hatch cũ, tạo lại
                    hatch.Erase();

                    var hatch2 = new Hatch();
                    hatch2.SetDatabaseDefaults(db);
                    hatch2.PatternScale = 1.0;
                    hatch2.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                    hatch2.HatchStyle = HatchStyle.Normal;
                    hatch2.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);

                    hatchId = blockRecord.AppendEntity(hatch2);
                    tr.AddNewlyCreatedDBObject(hatch2, true);

                    // Fallback: join curves thành polylines faceted rồi tạo loop
                    var facetedBoundaries = JoinCurvesToClosedPolylines(allCurves);
                    if (facetedBoundaries.Count > 0)
                    {
                        // Lấy polyline bao ngoài cùng (diện tích lớn nhất)
                        var outermost = facetedBoundaries.OrderByDescending(p => p.Area).First();

                        ObjectId plId = blockRecord.AppendEntity(outermost);
                        tr.AddNewlyCreatedDBObject(outermost, true);

                        var fallbackLoop = new ObjectIdCollection { plId };
                        hatch2.AppendLoop(HatchLoopTypes.Outermost, fallbackLoop);
                        hatch2.EvaluateHatch(true);

                        outermost.Erase(); // Xóa polyline phụ

                        // Cleanup remaining
                        foreach (var pl in facetedBoundaries)
                        {
                            if (pl != outermost && !pl.IsDisposed) pl.Dispose();
                        }
                    }
                }
                catch { }
            }

            // Xóa tất cả boundary curves tạm thời (chỉ giữ hatch)
            foreach (var curveId in tempBoundaryIds)
            {
                try
                {
                    var tempCurve = tr.GetObject(curveId, OpenMode.ForWrite) as Entity;
                    if (tempCurve != null && !tempCurve.IsErased) tempCurve.Erase();
                }
                catch { }
            }

            // Đặt DrawOrder hatch xuống dưới cùng
            SetDrawOrderToBottom(blockRecord, tr, hatchId);
        }

        // =====================================================================
        // FALLBACK: JOIN CURVES TO POLYLINES (khi Region loop thất bại)
        // =====================================================================

        private static List<Polyline> JoinCurvesToClosedPolylines(List<Curve> curves)
        {
            List<Polyline> results = new List<Polyline>();

            // Chuyển đổi tất cả Curve thành Polyline với 64 segments cho đường cong
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

            // Với các đường cong (Arc, Circle, Ellipse, Spline)
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
