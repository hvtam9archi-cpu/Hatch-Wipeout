using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using HatchWipeout.Logic;
using System;

[assembly: CommandClass(typeof(HatchWipeout.Commands.Commands))]

namespace HatchWipeout.Commands
{
    public class Commands
    {
        /// <summary>
        /// Lệnh TH: Chọn Block Reference → tạo Hatch Solid (Boundary Extraction) bên trong Block Definition.
        /// </summary>
        [CommandMethod("TH")]
        public void BlockHatchCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                // Chỉ cho phép chọn Block Reference (INSERT)
                ed.WriteMessage("\n>> Chọn các Block Reference để tạo Hatch Solid (Boundary Extraction): ");
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                });

                var selResult = ed.GetSelection(filter);
                if (selResult.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\nKhông có đối tượng nào được chọn.");
                    return;
                }

                var objectIds = selResult.Value.GetObjectIds();

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        int processedCount = BlockHatchLogic.Execute(db, tr, objectIds);
                        
                        // Cập nhật hiển thị (Graphics) cho toàn bộ các Block Reference được chọn
                        foreach (ObjectId blockRefId in objectIds)
                        {
                            var blockRef = tr.GetObject(blockRefId, OpenMode.ForWrite) as BlockReference;
                            if (blockRef != null)
                            {
                                blockRef.RecordGraphicsModified(true);
                            }
                        }

                        tr.Commit();
                        
                        // Force AutoCAD vẽ lại màn hình làm việc (Regen) để lập tức hiển thị Hatch vừa chèn
                        ed.Regen();
                        
                        ed.WriteMessage($"\nHoàn tất! Đã tạo Hatch cho {processedCount} block definition.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nLỗi: {ex.Message}\n{ex.StackTrace}");
            }
        }
        /// <summary>
        /// Lệnh TW: Chọn Block Reference → tạo Wipeout (Boundary Extraction) bên trong Block Definition.
        /// </summary>
        [CommandMethod("TW")]
        public void BlockWipeoutCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                ed.WriteMessage("\n>> Chọn các Block Reference để tạo Wipeout (Boundary Extraction): ");
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                });

                var selResult = ed.GetSelection(filter);
                if (selResult.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\nKhông có đối tượng nào được chọn.");
                    return;
                }

                var objectIds = selResult.Value.GetObjectIds();

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        int processedCount = BlockWipeoutLogic.Execute(db, tr, objectIds);

                        // Cập nhật hiển thị (Graphics) cho toàn bộ các Block Reference được chọn
                        foreach (ObjectId blockRefId in objectIds)
                        {
                            var blockRef = tr.GetObject(blockRefId, OpenMode.ForWrite) as BlockReference;
                            if (blockRef != null)
                            {
                                blockRef.RecordGraphicsModified(true);
                            }
                        }

                        tr.Commit();

                        // Force AutoCAD vẽ lại màn hình
                        ed.Regen();

                        ed.WriteMessage($"\nHoàn tất! Đã tạo Wipeout cho {processedCount} block definition.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nLỗi: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Lệnh XH: Chọn Block Reference → xóa Hatch và Wipeout bên trong Block Definition.
        /// </summary>
        [CommandMethod("XH")]
        public void BlockRemoveHatchWipeoutCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                ed.WriteMessage("\n>> Chọn các Block Reference để xóa Hatch/Wipeout: ");
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                });

                var selResult = ed.GetSelection(filter);
                if (selResult.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\nKhông có đối tượng nào được chọn.");
                    return;
                }

                var objectIds = selResult.Value.GetObjectIds();

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        int processedCount = BlockRemoveHatchWipeoutLogic.Execute(db, tr, objectIds);

                        // Cập nhật hiển thị (Graphics) cho toàn bộ các Block Reference được chọn
                        foreach (ObjectId blockRefId in objectIds)
                        {
                            var blockRef = tr.GetObject(blockRefId, OpenMode.ForWrite) as BlockReference;
                            if (blockRef != null)
                            {
                                blockRef.RecordGraphicsModified(true);
                            }
                        }

                        tr.Commit();

                        // Force AutoCAD vẽ lại màn hình
                        ed.Regen();

                        ed.WriteMessage($"\nHoàn tất! Đã xóa Hatch/Wipeout khỏi {processedCount} block definition.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nLỗi: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
