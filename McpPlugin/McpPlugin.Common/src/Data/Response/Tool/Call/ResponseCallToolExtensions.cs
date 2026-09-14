/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Uco.Framework.Common.Model
{
    public static class ResponseCallToolExtensions
    {
        public static ResponseCallTool Log(this ResponseCallTool target, ILogger logger, Exception? ex = null)
        {
            if (target.Status == ResponseStatus.Error)
                logger.LogError(ex, $"Error Response to AI:\n{target.GetMessage()}");
            else if (target.Status == ResponseStatus.Success)
                logger.LogInformation(ex, $"Success Response to AI:\n{target.GetMessage()}");
            else if (target.Status == ResponseStatus.Processing)
                logger.LogInformation(ex, $"Processing Response to AI:\n{target.GetMessage()}");

            return target;
        }

        public static ResponseData<ResponseCallTool> Pack(this ResponseCallTool target, string requestId, string? message = null)
        {
            ResponseData<ResponseCallTool> packed;
            if (target.Status == ResponseStatus.Error)
                packed = ResponseData<ResponseCallTool>.Error(requestId, message ?? target.GetMessage() ?? "Tool execution error.")
                    .SetData(target);
            else if (target.Status == ResponseStatus.Success)
                packed = ResponseData<ResponseCallTool>.Success(requestId, message ?? target.GetMessage() ?? "Tool executed successfully.")
                    .SetData(target);
            else if (target.Status == ResponseStatus.Processing)
                packed = ResponseData<ResponseCallTool>.Processing(requestId, message ?? target.GetMessage() ?? "Tool is processing.")
                    .SetData(target);
            else
                packed = ResponseData<ResponseCallTool>.Error(requestId, $"Unknown tool status `{target.Status}`.")
                    .SetData(target);

            // Keep a controlled structured error visible at the outer response
            // level as well as on the inner tool value.
            packed.StructuredError = target.StructuredError;
            return packed;
        }
    }
}
