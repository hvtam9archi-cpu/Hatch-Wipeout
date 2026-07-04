using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
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

        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr, Database db)
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage($"\n[TH-DEBUG] Báº¯t Ä‘áº§u xá»­ lÃ½ block: {blockRecord.Name}");

            var allCurves = new List<Curve>();
            CollectCurvesFromBlock(blockRecord, tr, Matrix3d.Identity, allCurves);

            if (allCurves.Count == 0)
            {
                ed.WriteMessage($"\n  - [Lá»—i] KhÃ´ng tÃ¬m tháº¥y Ä‘á»‘i tÆ°á»£ng há»£p lá»‡ trong block.");
                return false;
            }
            ed.WriteMessage($"\n  - Thu tháº­p Ä‘Æ°á»£c {allCurves.Count} Ä‘á»‘i tÆ°á»£ng dáº¡ng Ä‘Æ°á»ng.");

            List<Point2d> points = SamplePointsFromCurves(allCurves);
            ed.WriteMessage($"\n  - TrÃ­ch xuáº¥t Ä‘Æ°á»£c {points.Count} Ä‘iá»ƒm máº«u.");

            if (points.Count < 3)
            {
                ed.WriteMessage($"\n  - [Lá»—i] KhÃ´ng Ä‘á»§ Ä‘iá»ƒm Ä‘á»ƒ táº¡o Ä‘Æ°á»ng bao khÃ©p kÃ­n.");
                foreach (var c in allCurves) if (!c.IsDisposed) c.Dispose();
                return false;
            }

            List<Point2d> hullPoints = GetConvexHull(points);
            ed.WriteMessage($"\n  - Táº¡o Convex Hull vá»›i {hullPoints.Count} Ä‘á»‰nh.");

            Polyline hullPolyline = new Polyline();
            for (int i = 0; i < hullPoints.Count; i++)
            {
                hullPolyline.AddVertexAt(i, hullPoints[i], 0, 0, 0);
            }
            hullPolyline.Closed = true;

            bool result = false;
            try
            {
                CreateSolidHatchFromPolyline(blockRecord, tr, db, hullPolyline);
                ed.WriteMessage($"\n  - [ThÃ nh cÃ´ng] Táº¡o Solid Hatch thÃ nh cÃ´ng cho block: {blockRecord.Name}.");
                result = true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  - [Lá»—i] {ex.Message}");
            }
            finally
            {
                hullPolyline.Dispose();
                foreach (var c in allCurves) if (!c.IsDisposed) c.Dispose();
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

        private static List<Point2d> SamplePointsFromCurves(List<Curve> curves)
        {
            var points = new List<Point2d>();
            foreach (var curve in curves)
            {
                if (curve == null || curve.IsDisposed) continue;

                try
                {
                    points.Add(new Point2d(curve.StartPoint.X, curve.StartPoint.Y));
                    points.Add(new Point2d(curve.EndPoint.X, curve.EndPoint.Y));

                    if (curve is Line) continue;

                    if (curve is Polyline pl)
                    {
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            points.Add(pl.GetPoint2dAt(i));
                            
                            if (pl.GetBulgeAt(i) != 0 && i < pl.NumberOfVertices - 1)
                            {
                                try
                                {
                                    double startDist = pl.GetDistanceAtParameter(i);
                                    double endDist = pl.GetDistanceAtParameter(i + 1);
                                    double len = endDist - startDist;
                                    if (len > 1e-4)
                                    {
                                        int numSamples = Math.Max(2, (int)(len / 10.0));
                                        for (int j = 1; j < numSamples; j++)
                                        {
                                            double dist = startDist + j * len / numSamples;
                                            Point3d pt = pl.GetPointAtDist(dist);
                                            points.Add(new Point2d(pt.X, pt.Y));
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        SampleCurve(curve, points);
                    }
                }
                catch { }
            }
            return points;
        }
        
        private static void SampleCurve(Curve curve, List<Point2d> points)
        {
            try
            {
                double length = curve.GetDistanceAtParameter(curve.EndParam);
                if (length < 1e-4) return;
                
                int numSamples = Math.Max(2, (int)(length / 10.0));
                for (int i = 1; i < numSamples; i++)
                {
                    double dist = i * length / numSamples;
                    Point3d pt = curve.GetPointAtDist(dist);
                    points.Add(new Point2d(pt.X, pt.Y));
                }
            }
            catch { }
        }

        private static List<Point2d> GetConvexHull(List<Point2d> points)
        {
            if (points.Count <= 2) return new List<Point2d>(points);

            var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            if (sorted.Count <= 2) return sorted;

            double CrossProduct(Point2d o, Point2d a, Point2d b)
            {
                return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            }

            var lower = new List<Point2d>();
            foreach (var p in sorted)
            {
                while (lower.Count >= 2 && CrossProduct(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                {
                    lower.RemoveAt(lower.Count - 1);
                }
                lower.Add(p);
            }

            var upper = new List<Point2d>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var p = sorted[i];
                while (upper.Count >= 2 && CrossProduct(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                {
                    upper.RemoveAt(upper.Count - 1);
                }
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);

            lower.AddRange(upper);
            return lower;
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
