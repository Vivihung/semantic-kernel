// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.Planning;
using Microsoft.SemanticKernel.Planning.Sequential;
using Microsoft.SemanticKernel.Reliability;
using Microsoft.SemanticKernel.SkillDefinition;
using Microsoft.SemanticKernel.Skills.MsGraph;
using Microsoft.SemanticKernel.Skills.MsGraph.Connectors;
using Microsoft.SemanticKernel.Skills.MsGraph.Connectors.Client;
using Microsoft.SemanticKernel.Skills.OpenAPI.Authentication;
using SemanticKernel.Service.CopilotChat.Models;
using SemanticKernel.Service.CopilotChat.Options;
using SemanticKernel.Service.CopilotChat.Skills;
using SemanticKernel.Service.CopilotChat.Skills.ChatSkills;
using SemanticKernel.Service.CopilotChat.Storage;
using SemanticKernel.Service.Models;
using SemanticKernel.Service.Options;

namespace SemanticKernel.Service.CopilotChat.Controllers;

[ApiController]
public class DispatchController : ControllerBase, IDisposable
{
    private readonly ILogger<DispatchController> _logger;
    private readonly IKernel _kernel;
    private readonly SequentialPlanner _planner;
    private readonly MsGraphClientLoggingHandler _graphLoggingHandler;
    private IList<DelegatingHandler>? _graphMiddlewareHandlers;

    public DispatchController(
        IKernel kernel,
        IOptions<AIServiceOptions> aiServiceOptions,
        ChatMessageRepository chatMessageRepository,
        ChatSessionRepository chatSessionRepository,
        IOptions<PromptsOptions> promptOptions,
        IOptions<DocumentMemoryOptions> documentImportOptions,
        CopilotChatPlanner planner,
        ILogger<DispatchController> logger)
    {
        /* This way to create a new kernel will throws argument exception (logger is null) when importing skills.
         * Question: Why?
            this._kernel = new Kernel(
                    new SkillCollection(),
                    kernel.PromptTemplateEngine,
                    kernel.Memory,
                    this.CreateKernelConfig(aiServiceOptions.Value),
                    this._logger!);
        */

        this._logger = logger;

        // For MS Graph client
        this._graphLoggingHandler = new(this._logger);

        this._kernel = new KernelBuilder()
                .WithLogger(this._logger)
                .WithMemory(kernel.Memory)
                .WithConfiguration(this.CreateKernelConfig(aiServiceOptions.Value))
                .Build();

        this._planner = new SequentialPlanner(this._kernel, new SequentialPlannerConfig { RelevancyThreshold = 0.7 });

        // == Import skills ==
        // Semantic skill
        this._kernel.ImportSemanticSkillFromDirectory(Path.GetFullPath(Path.Combine(Path.GetFullPath(System.Reflection.Assembly.GetExecutingAssembly().Location), "..", "..", "..", "..", "..", "..", "..", "skills")), "SummarizeSkill", "WriterSkill");

        // Chat skill
        /*this._kernel.ImportSkill(new ChatSkill(
                kernel: kernel,
                chatMessageRepository: chatMessageRepository,
                chatSessionRepository: chatSessionRepository,
                promptOptions: promptOptions,
                documentImportOptions: documentImportOptions,
                planner: planner,
                logger: logger),
            nameof(ChatSkill));*/

        // Image skill
        this._kernel.ImportSkill(new ImageSkills(), nameof(ImageSkills));

        // Render skill
        this._kernel.ImportSkill(new RenderSkills(), nameof(RenderSkills));
    }

    [Authorize]
    [Route("dispatch")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DispatchAsync(
        [FromServices] IKernel appKernel,
        [FromBody] Ask ask,
        [FromHeader] OpenApiSkillsAuthHeaders openApiSkillsAuthHeaders)
    {
        this._logger.LogDebug("Dispatch request received.");

        // Put ask's variables in the context we will use.
        var contextVariables = new ContextVariables(ask.Input);
        foreach (var input in ask.Variables)
        {
            contextVariables.Set(input.Key, input.Value);
        }

        // Import plug-in skills on the fly because we can only register them with auth headers
        await this.RegisterPlannerSkillsAsync(this._kernel, openApiSkillsAuthHeaders, contextVariables);

        // Create a sequential planner with proper skill scope. It will figure out which skill to call next.
        // TODO: Would it be annoying in this use case if ask user confirmation everytime?
        // Question: When to clone a context, when not to?
        try
        {
            var goal = $"Provide a friendly reponse or fullfill '{ask.Input}'. Reach out to external APIs if capable, otherwise use ChatSkill. Then use the reponse as an input to find a proper renderer method. Stop creating steps once used a renderer.";
            var plan = await this._planner.CreatePlanAsync(goal);
            // contextVariables.Set("action", plan.ToJson());
            Console.WriteLine($"{plan.ToJson(true)}");
            // contextVariables.Update(plan.ToJson(true));

            // Manaully filter empty steps in the plan
            var filteredPlanSteps = plan.Steps.Where(step => !string.IsNullOrEmpty(step.Name)).ToList<Plan>();

            // Manually remove rendering step if that is the only remaining step. :(
            // Question: How should I update my prompt to make the model/planner do this for me?
            if (filteredPlanSteps.Count == 1 && filteredPlanSteps.ElementAt(0).Name == "RenderText")
            {
                filteredPlanSteps.RemoveAt(0);
            }

            ISKFunction? chatFunction = null;
            if (filteredPlanSteps.Count == 0)
            {
                // invoke chat function as the fallback skill
                try
                {
                    chatFunction = appKernel.Skills.GetFunction("ChatSkill", "Chat");
                    SKContext result = await appKernel.RunAsync(contextVariables, chatFunction);

                    return this.Ok(new AskResult { Value = result.Result, Variables = result.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)) });
                }
                catch (KernelException ke)
                {
                    this._logger.LogError("Failed to find ChatSkill.Chat on server: {0}", ke);

                    return this.NotFound("Failed to find ChatSkill.Chat on server");
                }
            }
            else
            {
                // Execute the plan with filtered steps.
                var clonedPlan = new Plan(goal, steps: filteredPlanSteps.ToArray<Plan>());

                // Invoke the plan.
                // Questions: Do we need user's approval for this type of dispatch? I don't feel it's necessary. However, when shall we surface the confirmation to empower human in the loop?
                // var completion = await clonedPlan.InvokeAsync();

                // Question: Do we need to store the plan in memory?
                /*await context.Memory.SaveInformationAsync(
                    collection: $"{chatId}-LearningSkill.LessonPlans",
                    text: plan.ToJson(true),
                    id: Guid.NewGuid().ToString(),
                    description: $"Plan for '{ask.Input}'",
                    additionalMetadata: plan.ToJson());*/

                return this.Ok(new AskResult { Value = clonedPlan.ToJson(true)/*, Variables = completion.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))*/ });
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception e)
        {
            Console.WriteLine($"*understands* I couldn't create a plan for '{ask.Input}'");
            Console.WriteLine(e);

            return this.BadRequest(e.Message);
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose of the object.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Question: IKernel doesn't have Dispose(), while Kernel is disposable?!
            // this._kernel.Dispose();

            this._graphLoggingHandler?.Dispose();

            if (this._graphMiddlewareHandlers != null)
            {
                foreach (IDisposable disposable in this._graphMiddlewareHandlers!)
                {
                    disposable.Dispose();
                }
            }
        }
    }

    // Temp hack to quickly setup a sperated kernel for this controller
    private KernelConfig CreateKernelConfig(AIServiceOptions options)
    {
        var kernelConfig = new KernelConfig();

        if (options.Type == AIServiceOptions.AIServiceType.AzureOpenAI)
        {
            kernelConfig.AddAzureChatCompletionService(options.Models.Completion, options.Endpoint, options.Key);
            kernelConfig.AddAzureTextEmbeddingGenerationService(options.Models.Embedding, options.Endpoint, options.Key);
        }
        else if (options.Type == AIServiceOptions.AIServiceType.OpenAI)
        {
            kernelConfig.AddOpenAIChatCompletionService(options.Models.Completion, options.Key);
            kernelConfig.AddOpenAITextEmbeddingGenerationService(options.Models.Embedding, options.Key);
        }
        else
        {
            throw new ArgumentException($"Invalid {nameof(options.Type)} value in '{AIServiceOptions.PropertyName}' settings.");
        }

        return kernelConfig;
    }

    /// <summary>
    /// Register plug-in skills
    /// Note: This is a duplication function from the ChatController.cs
    /// </summary>
    private async Task RegisterPlannerSkillsAsync(IKernel kernel, OpenApiSkillsAuthHeaders openApiSkillsAuthHeaders, ContextVariables variables)
    {
        // Register authenticated skills with the planner's kernel only if the request includes an auth header for the skill.

        // Klarna Shopping
        if (openApiSkillsAuthHeaders.KlarnaAuthentication != null)
        {
            // Register the Klarna shopping ChatGPT plugin with the planner's kernel.
            using DefaultHttpRetryHandler retryHandler = new(new HttpRetryConfig(), this._logger)
            {
                InnerHandler = new HttpClientHandler() { CheckCertificateRevocationList = true }
            };
            using HttpClient importHttpClient = new(retryHandler, false);
            importHttpClient.DefaultRequestHeaders.Add("User-Agent", "Microsoft.CopilotChat");
            await kernel.ImportChatGptPluginSkillFromUrlAsync("KlarnaShoppingSkill", new Uri("https://www.klarna.com/.well-known/ai-plugin.json"),
                importHttpClient);
        }

        // GitHub
        if (!string.IsNullOrWhiteSpace(openApiSkillsAuthHeaders.GithubAuthentication))
        {
            this._logger.LogInformation("Enabling GitHub skill.");
            BearerAuthenticationProvider authenticationProvider = new(() => Task.FromResult(openApiSkillsAuthHeaders.GithubAuthentication));
            await kernel.ImportOpenApiSkillFromFileAsync(
                skillName: "GitHubSkill",
                filePath: Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "CopilotChat", "Skills", "OpenApiSkills/GitHubSkill/openapi.json"),
                authCallback: authenticationProvider.AuthenticateRequestAsync);
        }

        // Jira
        if (!string.IsNullOrWhiteSpace(openApiSkillsAuthHeaders.JiraAuthentication))
        {
            this._logger.LogInformation("Registering Jira Skill");
            var authenticationProvider = new BasicAuthenticationProvider(() => { return Task.FromResult(openApiSkillsAuthHeaders.JiraAuthentication); });
            var hasServerUrlOverride = variables.Get("jira-server-url", out string serverUrlOverride);

            await kernel.ImportOpenApiSkillFromFileAsync(
                skillName: "JiraSkill",
                filePath: Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "CopilotChat", "Skills", "OpenApiSkills/JiraSkill/openapi.json"),
                authCallback: authenticationProvider.AuthenticateRequestAsync,
                serverUrlOverride: hasServerUrlOverride ? new Uri(serverUrlOverride) : null);
        }

        // Microsoft Graph
        if (!string.IsNullOrWhiteSpace(openApiSkillsAuthHeaders.GraphAuthentication))
        {
            this._logger.LogInformation("Enabling Microsoft Graph skill(s).");
            BearerAuthenticationProvider authenticationProvider = new(() => Task.FromResult(openApiSkillsAuthHeaders.GraphAuthentication));

            if (this._graphMiddlewareHandlers != null)
            {
                this._graphMiddlewareHandlers =
                GraphClientFactory.CreateDefaultHandlers(new DelegateAuthenticationProvider(authenticationProvider.AuthenticateRequestAsync));
                this._graphMiddlewareHandlers.Add(this._graphLoggingHandler);

                using (HttpClient graphHttpClient = GraphClientFactory.Create(this._graphMiddlewareHandlers))
                {
                    GraphServiceClient graphServiceClient = new(graphHttpClient);

                    kernel.ImportSkill(new TaskListSkill(new MicrosoftToDoConnector(graphServiceClient)), "todo");
                    kernel.ImportSkill(new CalendarSkill(new OutlookCalendarConnector(graphServiceClient)), "calendar");
                    kernel.ImportSkill(new EmailSkill(new OutlookMailConnector(graphServiceClient)), "email");
                }
            }
        }
    }
}
