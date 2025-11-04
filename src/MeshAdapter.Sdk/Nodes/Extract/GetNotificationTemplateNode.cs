using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

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
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetNotificationTemplateNodeConfiguration>();

        var notificationTemplateName = GetNotificationTemplateName(c, dataContext, nodeContext);

        var queryOptions = RtEntityQueryOptions.Create();
        queryOptions.AddFieldFilter(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.Equals, notificationTemplateName);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync<RtNotificationTemplate>(session, queryOptions);
        await session.CommitTransactionAsync();
        
        var notificationTemplate = r.Items.FirstOrDefault();
        if (notificationTemplate == null)
        {
            throw MeshAdapterPipelineExecutionException.NotificationTemplateNotFound(nodeContext, notificationTemplateName);
        }

        dataContext.SetValueByPath(c.SubjectTargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, notificationTemplate.SubjectTemplate);
        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, notificationTemplate.BodyTemplate);
        
        await next(dataContext, nodeContext);
    }

    private static string GetNotificationTemplateName(GetNotificationTemplateNodeConfiguration c, IDataContext dataContext, INodeContext nodeContext)
    {
        if (!string.IsNullOrEmpty(c.NotificationTemplateName))
        {
            return c.NotificationTemplateName;
        }
        
        if (!string.IsNullOrEmpty(c.NotificationTemplateNamePath))
        {
            var templateName = dataContext.GetSimpleValueByPath<string>(c.NotificationTemplateNamePath);
            if (string.IsNullOrEmpty(templateName))
            {
                throw MeshAdapterPipelineExecutionException.NotificationTemplateNameValueNull(nodeContext, c.NotificationTemplateNamePath);
            }
            return templateName;
        }
        
        throw MeshAdapterPipelineExecutionException.NotificationTemplateNameNotSet(nodeContext);
    }
}