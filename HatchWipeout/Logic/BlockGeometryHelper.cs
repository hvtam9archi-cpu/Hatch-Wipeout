using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HatchWipeout.Logic
{
    /// <summary>
    /// Helper class chứa các phương thức dùng chung giữa BlockHatchLogic và BlockWipeoutLogic.
    /// Tuân thủ DRY principle — tránh trùng lặp code.
    /// </summary>
    public static class BlockGeometryHelper
    {
        /// <summary>
        /// Lấy ObjectId của BlockTableRecord hiệu quả (hỗ trợ Dynamic Block).
        /// </summary>
        public static ObjectId GetEffectiveBlockTableRecordId(BlockReference blockRef)
        {
            try
            {
                if (blockRef.IsDynamicBlock)
                    return blockRef.DynamicBlockTableRecord;
                return blockRef.BlockTableRecord;
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[TH Tools] GetEffectiveBlockTableRecordId error: {ex.Message}");
                return blockRef.BlockTableRecord;
            }
        }

        /// <summary>
        /// Đệ quy thu thập tất cả Curve từ BlockTableRecord (bao gồm nested block),
        /// đã transform về WCS và flatten về XY.
        /// </summary>
        public static void CollectCurvesFromBlock(
            BlockTableRecord btr, Transaction tr, Matrix3d parentTransform,
            List<Curve> allCurves)
        {
            foreach (ObjectId id in btr)
            {
                Entity ent = null;
                Entity clonedEnt = null;
                try
                {
                    ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || !ent.Visible) continue;

                    clonedEnt = ent.Clone() as Entity;
                    if (clonedEnt == null) continue;

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
                        catch (System.Exception ex)
                        {
                            Debug.WriteLine($"[TH Tools] Nested block error: {ex.Message}");
                        }
                        clonedEnt.Dispose();
                        clonedEnt = null;
                    }
                    else if (clonedEnt is Curve curve)
                    {
                        Curve flat = FlattenCurve(curve);
                        if (flat != null) allCurves.Add(flat);
                        clonedEnt.Dispose();
                        clonedEnt = null;
                    }
                    else
                    {
                        clonedEnt.Dispose();
                        clonedEnt = null;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.WriteLine($"[TH Tools] CollectCurvesFromBlock error: {ex.Message}");
                    clonedEnt?.Dispose();
                }
            }
        }

        /// <summary>
        /// Chuyển Curve về mặt phẳng Z=0 (flatten XY).
        /// Lưu ý: Luôn dispose đối tượng clone bên trong để tránh memory leak.
        /// </summary>
        public static Curve FlattenCurve(Curve cv)
        {
            Curve c = null;
            try
            {
                c = cv.Clone() as Curve;
                if (c == null) return null;

                if (c is Polyline3d || c is Polyline2d)
                {
                    // Các loại polyline này đã ở dạng 2D, trả về bản sao mới
                    Curve result = cv.Clone() as Curve;
                    return result;
                }

                if (c is Line ln)
                {
                    ln.StartPoint = new Point3d(ln.StartPoint.X, ln.StartPoint.Y, 0);
                    ln.EndPoint = new Point3d(ln.EndPoint.X, ln.EndPoint.Y, 0);
                    Curve result = c;
                    c = null; // Tránh dispose ở finally
                    return result;
                }
                if (c is Polyline pl)
                {
                    pl.Elevation = 0;
                    pl.Normal = Vector3d.ZAxis;
                    Curve result = c;
                    c = null; // Tránh dispose ở finally
                    return result;
                }
                if (c is Circle cir)
                {
                    cir.Center = new Point3d(cir.Center.X, cir.Center.Y, 0);
                    cir.Normal = Vector3d.ZAxis;
                    Curve result = c;
                    c = null;
                    return result;
                }
                if (c is Arc arc)
                {
                    arc.Center = new Point3d(arc.Center.X, arc.Center.Y, 0);
                    arc.Normal = Vector3d.ZAxis;
                    Curve result = c;
                    c = null;
                    return result;
                }
                // Các loại Curve khác (Spline, Ellipse...) giữ nguyên bản clone đã transform
                Curve result2 = c;
                c = null; // Tránh dispose ở finally
                return result2;
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[TH Tools] FlattenCurve error: {ex.Message}");
                return null;
            }
            finally
            {
                // Dispose nếu chưa được chuyển giao
                if (c != null && !c.IsDisposed)
                    c.Dispose();
            }
        }

        /// <summary>
        /// Lấy (hoặc tạo mới) một Layer trong Database.
        /// Nếu layer đã tồn tại, trả về ObjectId của nó.
        /// Nếu chưa, tạo mới dựa trên properties của Layer "0".
        /// </summary>
        public static ObjectId GetOrCreateLayer(Database db, Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                return lt[layerName];
            }

            // Create new layer based on Layer 0 properties
            if (!lt.Has("0"))
            {
                Debug.WriteLine($"[TH Tools] Layer '0' không tồn tại! Không thể tạo layer: {layerName}");
                return ObjectId.Null;
            }

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

        /// <summary>
        /// Đặt thứ tự vẽ (DrawOrder) của entity xuống dưới cùng (Bottom)
        /// để Hatch/Wipeout không che khuất các đối tượng chính.
        /// </summary>
        public static void SetDrawOrderToBottom(BlockTableRecord blockRecord, Transaction tr, ObjectId entityId)
        {
            try
            {
                var drawOrderTable = tr.GetObject(blockRecord.DrawOrderTableId, OpenMode.ForWrite) as DrawOrderTable;
                if (drawOrderTable == null) return;

                var entityIds = new ObjectIdCollection { entityId };
                drawOrderTable.MoveToBottom(entityIds);
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[TH Tools] SetDrawOrderToBottom error: {ex.Message}");
            }
        }
    }
}
