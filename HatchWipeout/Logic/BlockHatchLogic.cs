using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;

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
        /// Thu thập Curve → Discretize → Fragment → Boundary Walk → Tạo Hatch Solid.
        /// </summary>
        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr, Database db)
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage($"\n[TH] Xử lý block: {blockRecord.Name}");

            // Bước 1: Thu thập tất cả Curve (bao gồm nested block, đã flatten về XY)
            var allCurves = new List<Curve>();
            CollectCurvesFromBlock(blockRecord, tr, Matrix3d.Identity, allCurves);

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
                    ed.WriteMessage($"\n  - Không tìm được đường biên ngoài. Thử fallback...");
                    return false;
                }

                ed.WriteMessage($"\n  - Tìm được {boundaries.Count} đường biên ngoài.");

                // Bước 5: Tạo Hatch Solid cho từng đường biên
                int hatchCount = 0;
                foreach (var boundary in boundaries)
                {
                    if (boundary.Count < 3) continue;

                    var polyline = new Polyline();
                    for (int i = 0; i < boundary.Count; i++)
                    {
                        polyline.AddVertexAt(i, boundary[i], 0, 0, 0);
                    }
                    polyline.Closed = true;

                    try
                    {
                        CreateSolidHatchFromPolyline(blockRecord, tr, db, polyline);
                        hatchCount++;
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  - Lỗi tạo Hatch: {ex.Message}");
                    }
                    finally
                    {
                        // Nếu polyline chưa được add vào database, dispose thủ công
                        if (!polyline.IsDisposed && polyline.ObjectId.IsNull)
                            polyline.Dispose();
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
            }
            finally
            {
                foreach (var c in allCurves)
                    if (c != null && !c.IsDisposed) c.Dispose();
            }

            return result;
        }

        private static ObjectId GetEffectiveBlockTableRecordId(BlockReference blockRef)
        {
            if (blockRef.IsDynamicBlock)
                return blockRef.DynamicBlockTableRecord;
            return blockRef.BlockTableRecord;
        }

        private static void CollectCurvesFromBlock(
            BlockTableRecord btr, Transaction tr, Matrix3d parentTransform,
            List<Curve> allCurves)
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
                                parentTransform * blockRef.BlockTransform, allCurves);
                        }
                    }
                    catch { }
                    clonedEnt.Dispose();
                }
                else if (clonedEnt is Curve curve)
                {
                    Curve flat = FlattenCurve(curve);
                    if (flat != null) allCurves.Add(flat);
                    clonedEnt.Dispose();
                }
                else
                {
                    clonedEnt.Dispose();
                }
            }
        }

        private static Curve FlattenCurve(Curve cv)
        {
            try
            {
                var c = cv.Clone() as Curve;
                if (c is Polyline3d || c is Polyline2d)
                {
                    return cv.Clone() as Curve;
                }
                
                if (c is Line ln)
                {
                    ln.StartPoint = new Point3d(ln.StartPoint.X, ln.StartPoint.Y, 0);
                    ln.EndPoint = new Point3d(ln.EndPoint.X, ln.EndPoint.Y, 0);
                    return ln;
                }
                if (c is Polyline pl)
                {
                    pl.Elevation = 0;
                    pl.Normal = Vector3d.ZAxis;
                    return pl;
                }
                if (c is Circle cir)
                {
                    cir.Center = new Point3d(cir.Center.X, cir.Center.Y, 0);
                    cir.Normal = Vector3d.ZAxis;
                    return cir;
                }
                if (c is Arc arc)
                {
                    arc.Center = new Point3d(arc.Center.X, arc.Center.Y, 0);
                    arc.Normal = Vector3d.ZAxis;
                    return arc;
                }
                return c;
            }
            catch { return null; }
        }

        private static void CreateSolidHatchFromPolyline(
            BlockTableRecord blockRecord, Transaction tr, Database db, Polyline polyline)
        {
            var hatch = new Hatch();
            hatch.SetDatabaseDefaults(db);
            hatch.PatternScale = 1.0;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.HatchStyle = HatchStyle.Normal;
            hatch.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);

            ObjectId hatchId = blockRecord.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            ObjectId curveId = blockRecord.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);

            var loopIds = new ObjectIdCollection();
            loopIds.Add(curveId);

            hatch.AppendLoop(HatchLoopTypes.External, loopIds);
            hatch.EvaluateHatch(true);

            try { polyline.Erase(); } catch { }

            SetDrawOrderToBottom(blockRecord, tr, hatchId);
        }

        private static void SetDrawOrderToBottom(BlockTableRecord blockRecord, Transaction tr, ObjectId entityId)
        {
            var drawOrderTable = tr.GetObject(blockRecord.DrawOrderTableId, OpenMode.ForWrite) as DrawOrderTable;
            if (drawOrderTable == null) return;

            var entityIds = new ObjectIdCollection { entityId };
            drawOrderTable.MoveToBottom(entityIds);
        }
    }
}
