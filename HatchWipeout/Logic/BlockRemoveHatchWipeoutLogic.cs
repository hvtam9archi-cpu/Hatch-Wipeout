using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;

namespace HatchWipeout.Logic
{
    public static class BlockRemoveHatchWipeoutLogic
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

                bool success = ProcessBlockRecord(blockRecord, tr);
                if (success) processedCount++;
            }

            return processedCount;
        }

        /// <summary>
        /// Xử lý chính cho một BlockTableRecord:
        /// Tìm tất cả Hatch và Wipeout và xóa chúng.
        /// </summary>
        private static bool ProcessBlockRecord(BlockTableRecord blockRecord, Transaction tr)
        {
            var idsToErase = new List<ObjectId>();

            foreach (ObjectId entId in blockRecord)
            {
                var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                if (ent != null && (ent is Hatch || ent is Wipeout))
                {
                    idsToErase.Add(entId);
                }
            }

            if (idsToErase.Count == 0) return false;

            foreach (ObjectId entId in idsToErase)
            {
                var ent = tr.GetObject(entId, OpenMode.ForWrite) as Entity;
                if (ent != null)
                {
                    ent.Erase();
                }
            }

            return true;
        }

        private static ObjectId GetEffectiveBlockTableRecordId(BlockReference blockRef)
        {
            if (blockRef.IsDynamicBlock)
                return blockRef.DynamicBlockTableRecord;
            return blockRef.BlockTableRecord;
        }
    }
}
