using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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

        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr, Database db)
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage($"\n[TW] Xử lý block: {blockRecord.Name}");

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
                        Debug.WriteLine($"[TH Tools] CreateWipeout error: {ex.Message}");
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
                Debug.WriteLine($"[TH Tools] ProcessBlockRecord error: {ex.Message}");
            }
            finally
            {
                foreach (var c in allCurves)
                    if (c != null && !c.IsDisposed) c.Dispose();
            }

            return result;
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
            if (wipeoutPoints.Count > 0 &&
                wipeoutPoints[0].GetDistanceTo(wipeoutPoints[wipeoutPoints.Count - 1]) > 1e-4)
            {
                wipeoutPoints.Add(wipeoutPoints[0]);
            }

            var wipeout = new Wipeout();
            wipeout.SetDatabaseDefaults(db);
            wipeout.SetFrom(wipeoutPoints, Vector3d.ZAxis);

            // Sử dụng layer riêng cho Wipeout
            BlockGeometryHelper.GetOrCreateLayer(db, tr, "TH_Wipeout");
            wipeout.Layer = "TH_Wipeout";

            ObjectId wipeoutId = blockRecord.AppendEntity(wipeout);
            tr.AddNewlyCreatedDBObject(wipeout, true);

            BlockGeometryHelper.SetDrawOrderToBottom(blockRecord, tr, wipeoutId);
        }
    }
}
