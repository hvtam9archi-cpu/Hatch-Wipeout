using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;

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

        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr, Database db)
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage($"\n[TW] Xử lý block: {blockRecord.Name}");

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
                List<Seg2d> segments = BoundaryEngine.DiscretizeCurves(allCurves);
                ed.WriteMessage($"\n  - Discretize: {segments.Count} đoạn thẳng.");

                if (segments.Count < 3)
                {
                    ed.WriteMessage($"\n  - Không đủ đoạn thẳng để tạo đường bao.");
                    return false;
                }

                List<Seg2d> fragmented = BoundaryEngine.FragmentAtIntersections(segments);
                ed.WriteMessage($"\n  - Fragment: {fragmented.Count} đoạn sau bẻ gãy.");

                List<List<Point2d>> boundaries = BoundaryEngine.FindAllOuterBoundaries(fragmented);

                if (boundaries.Count == 0)
                {
                    ed.WriteMessage($"\n  - Không tìm được đường biên ngoài.");
                    return false;
                }

                ed.WriteMessage($"\n  - Tìm được {boundaries.Count} đường biên ngoài.");

                int wipeoutCount = 0;
                foreach (var boundary in boundaries)
                {
                    if (boundary.Count < 3) continue;

                    try
                    {
                        CreateWipeoutFromPoints(blockRecord, tr, db, boundary);
                        wipeoutCount++;
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  - Lỗi tạo Wipeout: {ex.Message}");
                    }
                }

                if (wipeoutCount > 0)
                {
                    ed.WriteMessage($"\n  - [Thành công] Tạo {wipeoutCount} Wipeout cho block: {blockRecord.Name}.");
                    result = true;
                }
                else
                {
                    ed.WriteMessage($"\n  - [Thất bại] Không tạo được Wipeout nào.");
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

        private static void CreateWipeoutFromPoints(
            BlockTableRecord blockRecord, Transaction tr, Database db, List<Point2d> points)
        {
            var wipeoutPoints = new Point2dCollection();
            foreach (var pt in points)
            {
                wipeoutPoints.Add(pt);
            }
            
            // Đảm bảo đóng kín
            if (wipeoutPoints.Count > 0 && wipeoutPoints[0].GetDistanceTo(wipeoutPoints[wipeoutPoints.Count - 1]) > 1e-4)
            {
                wipeoutPoints.Add(wipeoutPoints[0]);
            }

            var wipeout = new Wipeout();
            wipeout.SetDatabaseDefaults(db);
            wipeout.SetFrom(wipeoutPoints, Vector3d.ZAxis);

            GetOrCreateLayer(db, tr, "TH_Hatch&Wipeout");
            wipeout.Layer = "TH_Hatch&Wipeout";

            ObjectId wipeoutId = blockRecord.AppendEntity(wipeout);
            tr.AddNewlyCreatedDBObject(wipeout, true);

            SetDrawOrderToBottom(blockRecord, tr, wipeoutId);
        }

        private static ObjectId GetOrCreateLayer(Database db, Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                return lt[layerName];
            }
            
            // Create new layer based on Layer 0 properties
            var layer0 = (LayerTableRecord)tr.GetObject(lt["0"], OpenMode.ForRead);
            
            lt.UpgradeOpen();
            var newLayer = new LayerTableRecord();
            newLayer.Name = layerName;
            newLayer.Color = layer0.Color;
            newLayer.LineWeight = layer0.LineWeight;
            newLayer.LinetypeObjectId = layer0.LinetypeObjectId;
            
            ObjectId layerId = lt.Add(newLayer);
            tr.AddNewlyCreatedDBObject(newLayer, true);
            return layerId;
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
