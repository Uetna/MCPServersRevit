using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class DeleteElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, IMcpEventHandler
    {
        // 执行结果
        public bool IsSuccess { get; private set; }

        // 成功删除的元素数量
        public int DeletedCount { get; private set; }

        // Ids that were not numeric or do not exist in the document
        public List<string> InvalidIds { get; private set; } = new List<string>();
        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public Exception LastError { get; private set; }

        public void PrepareForRaise()
        {
            LastError = null;
            _resetEvent.Reset();
        }
        // 要删除的元素ID数组
        public string[] ElementIds { get; set; }
        // 实现IWaitableExternalEventHandler接口
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
        return _resetEvent.WaitOne(timeoutMilliseconds);
        }
        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                DeletedCount = 0;
                if (ElementIds == null || ElementIds.Length == 0)
                {
                    IsSuccess = false;
                    return;
                }
                // 创建待删除元素ID集合
                List<ElementId> elementIdsToDelete = new List<ElementId>();
                List<string> invalidIds = new List<string>();
                foreach (var idStr in ElementIds)
                {
                    if (long.TryParse(idStr, out long elementIdValue))
                    {
                        var elementId = elementIdValue.ToElementId();
                        // 检查元素是否存在
                        if (doc.GetElement(elementId) != null)
                        {
                            elementIdsToDelete.Add(elementId);
                            continue;
                        }
                    }
                    invalidIds.Add(idStr);
                }
                InvalidIds = invalidIds;
                // 如果有可删除的元素，则执行删除
                if (elementIdsToDelete.Count > 0)
                {
                    using (var transaction = new Transaction(doc, "Delete Elements"))
                    {
                        transaction.Start();

                        // 批量删除元素
                        ICollection<ElementId> deletedIds = doc.Delete(elementIdsToDelete);
                        DeletedCount = deletedIds.Count;

                        transaction.Commit();
                    }
                    IsSuccess = true;
                }
                else
                {
                    LastError = new InvalidOperationException(
                        $"No existing elements to delete. Invalid or missing ids: {string.Join(", ", invalidIds)}");
                    IsSuccess = false;
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                IsSuccess = false;
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }
        public string GetName()
        {
            return "删除元素";
        }
    }
}
