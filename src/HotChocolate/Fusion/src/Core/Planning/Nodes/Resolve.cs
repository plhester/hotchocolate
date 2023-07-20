using HotChocolate.Execution.Processing;
using HotChocolate.Fusion.Clients;
using HotChocolate.Fusion.Execution;
using HotChocolate.Language;
using static HotChocolate.Fusion.Execution.ExecutorUtils;

namespace HotChocolate.Fusion.Planning;

/// <summary>
/// The resolver node is responsible for executing a GraphQL request on a subgraph.
/// </summary>
internal sealed class Resolve : ResolverNodeBase
{
    /// <summary>
    /// Initializes a new instance of <see cref="Resolve"/>.
    /// </summary>
    /// <param name="id">
    /// The unique id of this node.
    /// </param>
    /// <param name="subgraphName">
    /// The name of the subgraph on which this request handler executes.
    /// </param>
    /// <param name="document">
    /// The GraphQL request document.
    /// </param>
    /// <param name="selectionSet">
    /// The selection set for which this request provides a patch.
    /// </param>
    /// <param name="requires">
    /// The variables that this request handler requires to create a request.
    /// </param>
    /// <param name="path">
    /// The path to the data that this request handler needs to extract.
    /// </param>
    /// <param name="forwardedVariables">
    /// The variables that this request handler forwards to the subgraph.
    /// </param>
    /// <param name="transportFeatures">
    /// The transport features that are required by this node.
    /// </param>
    public Resolve(
        int id,
        string subgraphName,
        DocumentNode document,
        ISelectionSet selectionSet,
        IReadOnlyList<string> requires,
        IReadOnlyList<string> path,
        IReadOnlyList<string> forwardedVariables,
        TransportFeatures transportFeatures)
        : base(id, subgraphName, document, selectionSet, requires, path, forwardedVariables, transportFeatures)
    {
    }

    /// <summary>
    /// Gets the kind of this node.
    /// </summary>
    public override QueryPlanNodeKind Kind => QueryPlanNodeKind.Resolve;

    /// <summary>
    /// Executes this resolver node.
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// </param>
    /// <param name="state">
    /// The execution state.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// </param>
    protected override async Task OnExecuteAsync(
        FusionExecutionContext context,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        if (state.TryGetState(SelectionSet, out var workItems))
        {
            var schemaName = SubgraphName;
            var requests = new SubgraphGraphQLRequest[workItems.Count];

            // first we will create a request for all of our work items.
            for (var i = 0; i < workItems.Count; i++)
            {
                var workItem = workItems[i];
                TryInitializeWorkItem(context.QueryPlan, workItem);
                requests[i] = CreateRequest(
                    context.OperationContext.Variables,
                    workItem.VariableValues);
            }

            // once we have the requests, we will enqueue them for execution with the execution engine.
            // the execution engine will batch these requests if possible.
            var responses = await context.ExecuteAsync(
                schemaName,
                requests,
                cancellationToken)
                .ConfigureAwait(false);

            // before we extract the data from the responses we will enqueue the responses
            // for cleanup so that the memory can be released at the end of the execution.
            // Since we are not fully deserializing the responses we cannot release the memory here
            // but need to wait until the transport layer is finished and disposes the result.
            context.Result.RegisterForCleanup(
                responses,
                r =>
                {
                    for (var i = 0; i < r.Count; i++)
                    {
                        r[i].Dispose();
                    }
                    return default!;
                });

            for (var i = 0; i < requests.Length; i++)
            {
                var response = responses[i];
                var workItem = workItems[i];

                var data = UnwrapResult(response);
                var selectionSet = workItem.SelectionSet;
                var selectionResults = workItem.SelectionSetData;
                var exportKeys = workItem.ExportKeys;
                var variableValues = workItem.VariableValues;

                ExtractErrors(context.Result, response.Errors, context.ShowDebugInfo);

                // we extract the selection data from the request and add it to the
                // workItem results.
                ExtractSelectionResults(SelectionSet, schemaName, data, selectionResults);

                // next we need to extract any variables that we need for followup requests.
                ExtractVariables(data, context.QueryPlan, selectionSet, exportKeys, variableValues);
            }
        }
    }

    /// <summary>
    /// Executes this resolver node.
    /// </summary>
    /// <param name="context">
    /// The execution context.
    /// </param>
    /// <param name="state">
    /// The execution state.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// </param>
    protected override async Task OnExecuteNodesAsync(
        FusionExecutionContext context,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        if (state.ContainsState(SelectionSet))
        {
            await base.OnExecuteNodesAsync(context, state, cancellationToken).ConfigureAwait(false);
        }
    }
}
