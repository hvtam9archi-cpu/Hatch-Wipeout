using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HatchWipeout.Logic
{
    /// <summary>
    /// Logic tạo Hatch Solid bên trong Block Definition dựa trên Convex Hull
    /// của tất cả entities trong block.
    /// </summary>
    public static class BlockHatchLogic
    {
        /// <summary>
        /// Xử lý danh sách Block Reference được chọn:
        /// - Lọc ra các Block Definition duy nhất (theo tên)
        /// - Với mỗi Block Definition, thu thập điểm → tính Convex Hull → tạo Hatch Solid
        /// </summary>
        /// <returns>Số lượng Block Definition đã được xử lý thành công.</returns>
        public static int Execute(Database db, Transaction tr, ObjectId[] selectedBlockRefIds)
        {
            // Thu thập danh sách BlockTableRecord Id duy nhất (tránh xử lý trùng block name)
            var uniqueBlockRecordIds = new HashSet<ObjectId>();

            foreach (ObjectId blockRefId in selectedBlockRefIds)
            {
                var blockRef = tr.GetObject(blockRefId, OpenMode.ForRead) as BlockReference;
                if (blockRef == null) continue;

                // Lấy BlockTableRecord thực tế (xử lý cả Dynamic Block)
                ObjectId blockRecordId = GetEffectiveBlockTableRecordId(blockRef);
                uniqueBlockRecordIds.Add(blockRecordId);
            }

            int processedCount = 0;

            foreach (ObjectId blockRecordId in uniqueBlockRecordIds)
            {
                var blockRecord = tr.GetObject(blockRecordId, OpenMode.ForWrite) as BlockTableRecord;
                if (blockRecord == null) continue;

                // Thu thập tất cả các điểm đặc trưng từ entities trong block
                var allPoints = CollectPointsFromBlockRecord(blockRecord, tr, Matrix3d.Identity);
                if (allPoints.Count < 3) continue; // Cần ít nhất 3 điểm để tạo polygon

                // Tính Convex Hull
                var hullPoints = ComputeConvexHull(allPoints);
                if (hullPoints.Count < 3) continue;

                // Tạo Hatch Solid trong Block Definition
                CreateSolidHatchInBlock(blockRecord, tr, db, hullPoints);
                processedCount++;
            }

            return processedCount;
        }

        /// <summary>
        /// Lấy BlockTableRecordId thực tế, xử lý cả trường hợp Dynamic Block.
        /// </summary>
        private static ObjectId GetEffectiveBlockTableRecordId(BlockReference blockRef)
        {
            // Dynamic block: DynamicBlockTableRecord trỏ về block gốc
            if (blockRef.IsDynamicBlock)
            {
                return blockRef.DynamicBlockTableRecord;
            }
            return blockRef.BlockTableRecord;
        }

        // =====================================================================
        // THU THẬP ĐIỂM TỪ ENTITIES
        // =====================================================================

        /// <summary>
        /// Duyệt tất cả entities trong BlockTableRecord và thu thập các điểm đặc trưng.
        /// Hỗ trợ đệ quy cho Nested Block (BlockReference bên trong block).
        /// </summary>
        /// <param name="blockRecord">BlockTableRecord cần duyệt</param>
        /// <param name="tr">Transaction hiện hành</param>
        /// <param name="parentTransform">Ma trận biến đổi tích lũy (cho nested block)</param>
        private static List<Point2d> CollectPointsFromBlockRecord(
            BlockTableRecord blockRecord, Transaction tr, Matrix3d parentTransform)
        {
            var points = new List<Point2d>();

            foreach (ObjectId entityId in blockRecord)
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (entity == null || entity.Visible == false) continue;

                CollectPointsFromEntity(entity, tr, parentTransform, points);
            }

            return points;
        }

        /// <summary>
        /// Thu thập điểm đặc trưng từ một Entity cụ thể.
        /// </summary>
        private static void CollectPointsFromEntity(
            Entity entity, Transaction tr, Matrix3d transform, List<Point2d> points)
        {
            switch (entity)
            {
                case Line line:
                    AddTransformedPoint(line.StartPoint, transform, points);
                    AddTransformedPoint(line.EndPoint, transform, points);
                    break;

                case Polyline polyline:
                    CollectPointsFromPolyline(polyline, transform, points);
                    break;

                case Polyline2d polyline2d:
                    CollectPointsFromPolyline2d(polyline2d, tr, transform, points);
                    break;

                case Polyline3d polyline3d:
                    CollectPointsFromPolyline3d(polyline3d, tr, transform, points);
                    break;

                case Circle circle:
                    CollectPointsFromCircle(circle, transform, points);
                    break;

                case Arc arc:
                    CollectPointsFromArc(arc, transform, points);
                    break;

                case Ellipse ellipse:
                    CollectPointsFromEllipse(ellipse, transform, points);
                    break;

                case Spline spline:
                    CollectPointsFromSpline(spline, transform, points);
                    break;

                case DBText dbText:
                    CollectPointsFromExtents(dbText, transform, points);
                    break;

                case MText mText:
                    CollectPointsFromExtents(mText, transform, points);
                    break;

                case Solid solid:
                    CollectPointsFromSolid(solid, transform, points);
                    break;

                case Hatch hatch:
                    CollectPointsFromExtents(hatch, transform, points);
                    break;

                case BlockReference nestedBlockRef:
                    CollectPointsFromNestedBlock(nestedBlockRef, tr, transform, points);
                    break;

                default:
                    // Fallback: dùng GeometricExtents cho các entity không được xử lý riêng
                    CollectPointsFromExtents(entity, transform, points);
                    break;
            }
        }

        /// <summary>
        /// Biến đổi Point3d qua matrix rồi thêm vào danh sách dưới dạng Point2d.
        /// </summary>
        private static void AddTransformedPoint(Point3d point, Matrix3d transform, List<Point2d> points)
        {
            Point3d transformed = point.TransformBy(transform);
            points.Add(new Point2d(transformed.X, transformed.Y));
        }

        private static void CollectPointsFromPolyline(Polyline polyline, Matrix3d transform, List<Point2d> points)
        {
            for (int i = 0; i < polyline.NumberOfVertices; i++)
            {
                Point3d vertex = polyline.GetPoint3dAt(i);
                AddTransformedPoint(vertex, transform, points);

                // Nếu segment là arc (bulge != 0), lấy thêm điểm trên cung
                double bulge = polyline.GetBulgeAt(i);
                if (Math.Abs(bulge) > 1e-6 && i < polyline.NumberOfVertices - 1)
                {
                    Point3d nextVertex = polyline.GetPoint3dAt(i + 1);
                    var arcPoints = SampleArcFromBulge(vertex, nextVertex, bulge, 8);
                    foreach (var arcPt in arcPoints)
                    {
                        AddTransformedPoint(arcPt, transform, points);
                    }
                }
            }
        }

        private static void CollectPointsFromPolyline2d(Polyline2d polyline2d, Transaction tr,
            Matrix3d transform, List<Point2d> points)
        {
            foreach (ObjectId vertexId in polyline2d)
            {
                var vertex = tr.GetObject(vertexId, OpenMode.ForRead) as Vertex2d;
                if (vertex == null) continue;
                AddTransformedPoint(vertex.Position, transform, points);
            }
        }

        private static void CollectPointsFromPolyline3d(Polyline3d polyline3d, Transaction tr,
            Matrix3d transform, List<Point2d> points)
        {
            foreach (ObjectId vertexId in polyline3d)
            {
                var vertex = tr.GetObject(vertexId, OpenMode.ForRead) as PolylineVertex3d;
                if (vertex == null) continue;
                AddTransformedPoint(vertex.Position, transform, points);
            }
        }

        /// <summary>
        /// Circle: lấy 12 điểm rải đều trên đường tròn.
        /// </summary>
        private static void CollectPointsFromCircle(Circle circle, Matrix3d transform, List<Point2d> points)
        {
            const int sampleCount = 12;
            for (int i = 0; i < sampleCount; i++)
            {
                double angle = 2.0 * Math.PI * i / sampleCount;
                Point3d pt = new Point3d(
                    circle.Center.X + circle.Radius * Math.Cos(angle),
                    circle.Center.Y + circle.Radius * Math.Sin(angle),
                    circle.Center.Z);
                AddTransformedPoint(pt, transform, points);
            }
        }

        /// <summary>
        /// Arc: lấy điểm đầu, cuối và các điểm sample trên cung.
        /// </summary>
        private static void CollectPointsFromArc(Arc arc, Matrix3d transform, List<Point2d> points)
        {
            AddTransformedPoint(arc.StartPoint, transform, points);
            AddTransformedPoint(arc.EndPoint, transform, points);

            const int sampleCount = 8;
            double startAngle = arc.StartAngle;
            double endAngle = arc.EndAngle;
            double totalAngle = endAngle - startAngle;
            if (totalAngle < 0) totalAngle += 2.0 * Math.PI;

            for (int i = 1; i < sampleCount; i++)
            {
                double angle = startAngle + totalAngle * i / sampleCount;
                Point3d pt = new Point3d(
                    arc.Center.X + arc.Radius * Math.Cos(angle),
                    arc.Center.Y + arc.Radius * Math.Sin(angle),
                    arc.Center.Z);
                AddTransformedPoint(pt, transform, points);
            }
        }

        /// <summary>
        /// Ellipse: lấy 16 điểm rải đều trên ellipse.
        /// </summary>
        private static void CollectPointsFromEllipse(Ellipse ellipse, Matrix3d transform, List<Point2d> points)
        {
            const int sampleCount = 16;
            double startParam = ellipse.StartParam;
            double endParam = ellipse.EndParam;
            double totalParam = endParam - startParam;

            for (int i = 0; i <= sampleCount; i++)
            {
                double param = startParam + totalParam * i / sampleCount;
                Point3d pt = ellipse.GetPointAtParameter(param);
                AddTransformedPoint(pt, transform, points);
            }
        }

        /// <summary>
        /// Spline: lấy điểm control và sample trên spline.
        /// </summary>
        private static void CollectPointsFromSpline(Spline spline, Matrix3d transform, List<Point2d> points)
        {
            // Lấy control points
            for (int i = 0; i < spline.NumControlPoints; i++)
            {
                AddTransformedPoint(spline.GetControlPointAt(i), transform, points);
            }

            // Sample thêm điểm trên spline để chính xác hơn
            const int sampleCount = 16;
            double startParam = spline.StartParam;
            double endParam = spline.EndParam;
            double totalParam = endParam - startParam;

            for (int i = 0; i <= sampleCount; i++)
            {
                double param = startParam + totalParam * i / sampleCount;
                Point3d pt = spline.GetPointAtParameter(param);
                AddTransformedPoint(pt, transform, points);
            }
        }

        /// <summary>
        /// Solid (2D): lấy 4 đỉnh.
        /// </summary>
        private static void CollectPointsFromSolid(Solid solid, Matrix3d transform, List<Point2d> points)
        {
            for (short i = 0; i < 4; i++)
            {
                AddTransformedPoint(solid.GetPointAt(i), transform, points);
            }
        }

        /// <summary>
        /// Fallback: dùng GeometricExtents (bounding box) cho entity không có điểm rõ ràng.
        /// </summary>
        private static void CollectPointsFromExtents(Entity entity, Matrix3d transform, List<Point2d> points)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                Point3d minPt = extents.MinPoint;
                Point3d maxPt = extents.MaxPoint;

                AddTransformedPoint(minPt, transform, points);
                AddTransformedPoint(new Point3d(maxPt.X, minPt.Y, minPt.Z), transform, points);
                AddTransformedPoint(maxPt, transform, points);
                AddTransformedPoint(new Point3d(minPt.X, maxPt.Y, minPt.Z), transform, points);
            }
            catch
            {
                // Entity không có extents (VD: empty text) → bỏ qua
            }
        }

        /// <summary>
        /// Nested Block: đệ quy thu thập điểm với transform tích lũy.
        /// </summary>
        private static void CollectPointsFromNestedBlock(BlockReference nestedBlockRef, Transaction tr,
            Matrix3d parentTransform, List<Point2d> points)
        {
            // Tích lũy transform: parent * nested block transform
            Matrix3d combinedTransform = parentTransform * nestedBlockRef.BlockTransform;

            ObjectId nestedRecordId = GetEffectiveBlockTableRecordId(nestedBlockRef);
            var nestedRecord = tr.GetObject(nestedRecordId, OpenMode.ForRead) as BlockTableRecord;
            if (nestedRecord == null) return;

            foreach (ObjectId entityId in nestedRecord)
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (entity == null || entity.Visible == false) continue;

                CollectPointsFromEntity(entity, tr, combinedTransform, points);
            }
        }

        // =====================================================================
        // CONVEX HULL – Andrew's Monotone Chain Algorithm
        // =====================================================================

        /// <summary>
        /// Tính Convex Hull (bao lồi) bằng thuật toán Andrew's Monotone Chain.
        /// Trả về danh sách điểm theo thứ tự ngược chiều kim đồng hồ (CCW).
        /// </summary>
        public static List<Point2d> ComputeConvexHull(List<Point2d> inputPoints)
        {
            // Loại bỏ điểm trùng lặp
            var distinctPoints = inputPoints
                .Select(p => new Point2d(Math.Round(p.X, 6), Math.Round(p.Y, 6)))
                .Distinct(new Point2dComparer())
                .ToList();

            int n = distinctPoints.Count;
            if (n < 3) return distinctPoints;

            // Sắp xếp theo X, nếu X bằng nhau thì theo Y
            distinctPoints.Sort((a, b) =>
            {
                int cmpX = a.X.CompareTo(b.X);
                return cmpX != 0 ? cmpX : a.Y.CompareTo(b.Y);
            });

            var hull = new Point2d[2 * n];
            int k = 0;

            // Dựng Lower Hull (trái → phải)
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross(hull[k - 2], hull[k - 1], distinctPoints[i]) <= 0)
                    k--;
                hull[k++] = distinctPoints[i];
            }

            // Dựng Upper Hull (phải → trái)
            int lowerSize = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lowerSize && Cross(hull[k - 2], hull[k - 1], distinctPoints[i]) <= 0)
                    k--;
                hull[k++] = distinctPoints[i];
            }

            // k-1 vì điểm đầu bị lặp lại ở cuối
            return hull.Take(k - 1).ToList();
        }

        /// <summary>
        /// Cross product 2D: (B-A) x (C-A).
        /// Dương = CCW, Âm = CW, 0 = collinear.
        /// </summary>
        private static double Cross(Point2d a, Point2d b, Point2d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        // =====================================================================
        // TẠO HATCH SOLID TRONG BLOCK DEFINITION
        // =====================================================================

        /// <summary>
        /// Tạo Hatch Solid bên trong BlockTableRecord từ danh sách điểm Convex Hull.
        /// Hatch sẽ được đặt DrawOrder xuống dưới cùng.
        /// </summary>
        private static void CreateSolidHatchInBlock(
            BlockTableRecord blockRecord, Transaction tr, Database db, List<Point2d> hullPoints)
        {
            // Tạo Hatch object
            var hatch = new Hatch();
            hatch.SetDatabaseDefaults(db);
            hatch.PatternScale = 1.0;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.HatchStyle = HatchStyle.Normal;
            hatch.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256); // ByLayer

            // Thêm Hatch vào Block Definition
            ObjectId hatchId = blockRecord.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            // Tạo Polyline loop từ Convex Hull points
            var bulges = new DoubleCollection();
            var vertices = new Point2dCollection();

            foreach (var pt in hullPoints)
            {
                vertices.Add(pt);
                bulges.Add(0.0); // Tất cả segment là đường thẳng
            }

            hatch.AppendLoop(HatchLoopTypes.Outermost, vertices, bulges);
            hatch.EvaluateHatch(true);

            // Đặt DrawOrder hatch xuống dưới cùng trong block
            SetDrawOrderToBottom(blockRecord, tr, hatchId);
        }

        /// <summary>
        /// Đặt entity xuống dưới cùng trong DrawOrder của BlockTableRecord.
        /// </summary>
        private static void SetDrawOrderToBottom(BlockTableRecord blockRecord, Transaction tr, ObjectId entityId)
        {
            var drawOrderTable = tr.GetObject(blockRecord.DrawOrderTableId, OpenMode.ForWrite) as DrawOrderTable;
            if (drawOrderTable == null) return;

            var entityIds = new ObjectIdCollection { entityId };
            drawOrderTable.MoveToBottom(entityIds);
        }

        // =====================================================================
        // HELPER: ARC SAMPLING
        // =====================================================================

        /// <summary>
        /// Tạo các điểm sample trên cung arc được định nghĩa bởi bulge giữa 2 vertex.
        /// </summary>
        private static List<Point3d> SampleArcFromBulge(Point3d startPt, Point3d endPt, double bulge, int sampleCount)
        {
            var result = new List<Point3d>();

            // Tính tâm và bán kính từ bulge
            double dx = endPt.X - startPt.X;
            double dy = endPt.Y - startPt.Y;
            double chordLength = Math.Sqrt(dx * dx + dy * dy);
            if (chordLength < 1e-10) return result;

            double sagitta = Math.Abs(bulge) * chordLength / 2.0;
            double radius = (chordLength * chordLength / 4.0 + sagitta * sagitta) / (2.0 * sagitta);

            // Midpoint của chord
            double midX = (startPt.X + endPt.X) / 2.0;
            double midY = (startPt.Y + endPt.Y) / 2.0;

            // Normal vector vuông góc với chord
            double nx = -dy / chordLength;
            double ny = dx / chordLength;

            // Offset từ midpoint đến center
            double offset = radius - sagitta;
            double sign = bulge > 0 ? 1.0 : -1.0;

            double centerX = midX + sign * offset * nx;
            double centerY = midY + sign * offset * ny;

            // Góc bắt đầu và kết thúc
            double startAngle = Math.Atan2(startPt.Y - centerY, startPt.X - centerX);
            double endAngle = Math.Atan2(endPt.Y - centerY, endPt.X - centerX);

            // Xác định hướng quét
            double sweepAngle = endAngle - startAngle;
            if (bulge > 0 && sweepAngle < 0) sweepAngle += 2.0 * Math.PI;
            if (bulge < 0 && sweepAngle > 0) sweepAngle -= 2.0 * Math.PI;

            for (int i = 1; i < sampleCount; i++)
            {
                double t = (double)i / sampleCount;
                double angle = startAngle + sweepAngle * t;
                result.Add(new Point3d(
                    centerX + radius * Math.Cos(angle),
                    centerY + radius * Math.Sin(angle),
                    startPt.Z));
            }

            return result;
        }

        // =====================================================================
        // HELPER: Point2d Comparer (cho Distinct)
        // =====================================================================

        private class Point2dComparer : IEqualityComparer<Point2d>
        {
            public bool Equals(Point2d a, Point2d b)
            {
                return Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
            }

            public int GetHashCode(Point2d p)
            {
                // Round để các điểm gần nhau có cùng hash
                long hx = (long)(p.X * 1000000);
                long hy = (long)(p.Y * 1000000);
                return hx.GetHashCode() ^ (hy.GetHashCode() << 16);
            }
        }
    }
}
