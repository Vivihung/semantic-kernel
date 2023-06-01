// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;

namespace SemanticKernel.Service.CopilotChat.Skills;

public class RenderSkills
{
    [SKFunction("Render an image")]
    [SKFunctionName("RenderImage")]
    public async Task<SKContext> RenderImageAsync(SKContext context)
    {
        Console.WriteLine("Render an image.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Render a text")]
    [SKFunctionName("RenderText")]
    public async Task<SKContext> RenderTextAsync(SKContext context)
    {
        Console.WriteLine("Render a text.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Render a rich text")]
    [SKFunctionName("RenderRichText")]
    public async Task<SKContext> RenderRichTextAsync(SKContext context)
    {
        Console.WriteLine("Render a rich text.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Render a person")]
    [SKFunctionName("RenderPerson")]
    public async Task<SKContext> RenderPersonAsync(SKContext context)
    {
        Console.WriteLine("Rendering a person.");

        return await Task.FromResult<SKContext>(context);
    }

    [SKFunction("Render program code")]
    [SKFunctionName("RenderCode")]
    public async Task<SKContext> RenderCodeAsync(SKContext context)
    {
        Console.WriteLine("Rendering code.");

        return await Task.FromResult<SKContext>(context);
    }
}
