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
        /// Lệnh BKHATCH: Chọn Block Reference → tạo Hatch Solid (Convex Hull) bên trong Block Definition.
        /// </summary>
        [CommandMethod("BKHATCH")]
        public void BlockHatchCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                // Chỉ cho phép chọn Block Reference (INSERT)
                ed.WriteMessage("\n>> Chọn các Block Reference để tạo Hatch Solid theo Convex Hull: ");
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
    }
}
