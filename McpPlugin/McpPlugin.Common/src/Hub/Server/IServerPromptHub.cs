/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework.Common.Hub.Server
{
    public interface IServerPromptHub
    {
        Task<ResponseData> NotifyAboutUpdatedPrompts(RequestPromptsUpdated request);
    }
}
