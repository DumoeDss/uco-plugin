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
using System.Reflection;

namespace com.AtelierAI.Uco.Framework
{
    public class SkillMemberData
    {
        public string Name => Attribute.Name;
        public Type ClassType { get; set; }
        public MemberInfo MemberInfo { get; set; }
        public UcoSkillAttribute Attribute { get; set; }
        public string Content { get; set; }

        public SkillMemberData(Type classType, MemberInfo memberInfo, UcoSkillAttribute attribute, string content)
        {
            ClassType = classType;
            MemberInfo = memberInfo;
            Attribute = attribute;
            Content = content;
        }
    }
}
