using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

/// <summary>
/// Get a notification template from the database by name
/// </summary>
/// <param name="next">Delegate to the next node in the pipeline</param>
/// <param name="etlContext">Node context</param>
[NodeConfiguration(typeof(GetNotificationTemplateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetNotificationTemplateNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<GetNotificationTemplateNodeConfiguration>();

        var dataQueryOperation = DataQueryOperation.Create();
        dataQueryOperation.AddFieldFilter(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.Equals, c.NotificationTemplateName);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync<RtNotificationTemplate>(session, dataQueryOperation);
        await session.CommitTransactionAsync();
        
        var notificationTemplate = r.Items.FirstOrDefault();
        if (notificationTemplate == null)
        {
            dataContext.NodeContext.Error("Notification template not found");
            return;
        }

        dataContext.SetValueByPath(c.SubjectTargetPath, c.TargetValueKind, c.TargetValueWriteMode, notificationTemplate.SubjectTemplate);
        dataContext.SetValueByPath(c.TargetPath, c.TargetValueKind, c.TargetValueWriteMode, notificationTemplate.BodyTemplate);
        
        await next(dataContext);
    }
}