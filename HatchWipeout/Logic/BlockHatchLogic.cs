using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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

                ObjectId blockRecordId = BlockGeometryHelper.GetEffectiveBlockTableRecordId(blockRef);
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
        /// Thu thập Curve → Discretize → Fragment → Boundary Walk → Tạo Hatch Solid.
        /// </summary>
        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr, Database db)
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage($"\n[TH] Xử lý block: {blockRecord.Name}");

            // Bước 1: Thu thập tất cả Curve (bao gồm nested block, đã flatten về XY)
            var allCurves = new List<Curve>();
            BlockGeometryHelper.CollectCurvesFromBlock(blockRecord, tr, Matrix3d.Identity, allCurves);

            if (allCurves.Count == 0)
            {
                ed.WriteMessage($"\n  - Không tìm thấy đối tượng hợp lệ trong block.");
                return false;
            }
            ed.WriteMessage($"\n  - Thu thập được {allCurves.Count} đối tượng dạng đường.");

            bool result = false;
            try
            {
                // Bước 2: Discretization — chuyển tất cả Curve thành đoạn thẳng (60 phần/đường cong)
                List<Seg2d> segments = BoundaryEngine.DiscretizeCurves(allCurves);
                ed.WriteMessage($"\n  - Discretize: {segments.Count} đoạn thẳng.");

                if (segments.Count < 3)
                {
                    ed.WriteMessage($"\n  - Không đủ đoạn thẳng để tạo đường bao.");
                    return false;
                }

                // Bước 3: Fragmentation — tìm giao điểm và bẻ gãy (Spatial Grid tối ưu)
                List<Seg2d> fragmented = BoundaryEngine.FragmentAtIntersections(segments);
                ed.WriteMessage($"\n  - Fragment: {fragmented.Count} đoạn sau bẻ gãy.");

                // Bước 4: Boundary Walk — truy vết biên ngoài cùng
                List<List<Point2d>> boundaries = BoundaryEngine.FindAllOuterBoundaries(fragmented);

                if (boundaries.Count == 0)
                {
                    ed.WriteMessage($"\n  - Không tìm được đường biên ngoài.");
                    return false;
                }

                ed.WriteMessage($"\n  - Tìm được {boundaries.Count} đường biên ngoài.");

                // Bước 5: Tạo và lọc Polyline (bỏ các Polyline nhỏ nằm bên trong Polyline lớn)
                var boundaryDataList = new List<BoundaryData>();
                foreach (var boundary in boundaries)
                {
                    if (boundary.Count < 3) continue;

                    var polyline = new Polyline();
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    for (int i = 0; i < boundary.Count; i++)
                    {
                        var pt = boundary[i];
                        polyline.AddVertexAt(i, pt, 0, 0, 0);
                        if (pt.X < minX) minX = pt.X;
                        if (pt.Y < minY) minY = pt.Y;
                        if (pt.X > maxX) maxX = pt.X;
                        if (pt.Y > maxY) maxY = pt.Y;
                    }
                    polyline.Closed = true;

                    boundaryDataList.Add(new BoundaryData
                    {
                        Poly = polyline,
                        Area = polyline.Area, // Chỉ tính Area 1 lần
                        Extents = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0)),
                        StartPoint = new Point3d(boundary[0].X, boundary[0].Y, 0),
                        IsRemoved = false
                    });
                }

                // Sắp xếp theo diện tích giảm dần để xét polyline to trước
                boundaryDataList.Sort((a, b) => b.Area.CompareTo(a.Area));

                for (int i = 0; i < boundaryDataList.Count; i++)
                {
                    var innerData = boundaryDataList[i];
                    if (innerData.IsRemoved) continue;

                    for (int j = 0; j < i; j++) // Chỉ check với các polyline lớn hơn
                    {
                        var outerData = boundaryDataList[j];
                        if (outerData.IsRemoved) continue;

                        // Kiểm tra bao hình trước cho nhanh (dùng cache, không gọi lại API)
                        if (IsInsideExtents(outerData.Extents, innerData.Extents))
                        {
                            // Kiểm tra điểm của inner có nằm trong outer không
                            if (IsPointInsidePolyline(outerData.Poly, innerData.StartPoint))
                            {
                                innerData.IsRemoved = true;
                                break;
                            }
                        }
                    }
                }

                int hatchCount = 0;
                foreach (var data in boundaryDataList)
                {
                    if (data.IsRemoved)
                    {
                        data.Poly.Dispose();
                        continue;
                    }

                    try
                    {
                        CreateSolidHatchFromPolyline(blockRecord, tr, db, data.Poly);
                        hatchCount++;
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  - Lỗi tạo Hatch: {ex.Message}");
                        Debug.WriteLine($"[TH Tools] CreateHatch error: {ex.Message}");
                    }
                    finally
                    {
                        // Nếu polyline chưa được add vào database, dispose thủ công
                        if (!data.Poly.IsDisposed && data.Poly.ObjectId.IsNull)
                            data.Poly.Dispose();
                    }
                }

                if (hatchCount > 0)
                {
                    ed.WriteMessage($"\n  - [Thành công] Tạo {hatchCount} Solid Hatch cho block: {blockRecord.Name}.");
                    result = true;
                }
                else
                {
                    ed.WriteMessage($"\n  - [Thất bại] Không tạo được Hatch nào.");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  - [Lỗi] {ex.Message}");
                Debug.WriteLine($"[TH Tools] ProcessBlockRecord error: {ex.Message}");
            }
            finally
            {
                foreach (var c in allCurves)
                    if (c != null && !c.IsDisposed) c.Dispose();
            }

            return result;
        }

        private static void CreateSolidHatchFromPolyline(
            BlockTableRecord blockRecord, Transaction tr, Database db, Polyline polyline)
        {
            var hatch = new Hatch();
            hatch.SetDatabaseDefaults(db);
            hatch.PatternScale = 1.0;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.HatchStyle = HatchStyle.Normal;

            // Sử dụng layer riêng cho Hatch
            BlockGeometryHelper.GetOrCreateLayer(db, tr, "TH_Hatch");
            hatch.Layer = "TH_Hatch";
            hatch.Color = Color.FromRgb(222, 222, 222);

            ObjectId hatchId = blockRecord.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            ObjectId curveId = blockRecord.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);

            var loopIds = new ObjectIdCollection();
            loopIds.Add(curveId);

            hatch.AppendLoop(HatchLoopTypes.External, loopIds);
            hatch.EvaluateHatch(true);

            try
            {
                if (!polyline.IsDisposed)
                    polyline.Erase();
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[TH Tools] Erase polyline error: {ex.Message}");
            }

            BlockGeometryHelper.SetDrawOrderToBottom(blockRecord, tr, hatchId);
        }

        private class BoundaryData
        {
            public Polyline Poly { get; set; }
            public double Area { get; set; }
            public Extents3d Extents { get; set; }
            public Point3d StartPoint { get; set; }
            public bool IsRemoved { get; set; }
        }

        private static bool IsInsideExtents(Extents3d outer, Extents3d inner)
        {
            const double tol = 1e-6;
            return inner.MinPoint.X >= outer.MinPoint.X - tol &&
                   inner.MinPoint.Y >= outer.MinPoint.Y - tol &&
                   inner.MaxPoint.X <= outer.MaxPoint.X + tol &&
                   inner.MaxPoint.Y <= outer.MaxPoint.Y + tol;
        }

        private static bool IsPointInsidePolyline(Polyline pl, Point3d pt)
        {
            int intersectCount = 0;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                Point3d p1 = pl.GetPoint3dAt(i);
                Point3d p2 = pl.GetPoint3dAt((i + 1) % n);
                
                if ((p1.Y > pt.Y) != (p2.Y > pt.Y))
                {
                    double xIntersect = (p2.X - p1.X) * (pt.Y - p1.Y) / (p2.Y - p1.Y) + p1.X;
                    if (pt.X < xIntersect)
                    {
                        intersectCount++;
                    }
                }
            }
            return (intersectCount % 2) == 1;
        }
    }
}
