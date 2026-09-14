#!/usr/bin/env bash
#
# Build the three internal framework projects (ReflectorNet, McpPlugin.Common,
# McpPlugin) from source and deploy the netstandard2.1 DLLs as static plugin
# assets.
#
# The Unity plugin consumes three internal framework DLLs at compile time via
# asmdef precompiledReferences: ReflectorNet.dll, McpPlugin.Common.dll,
# McpPlugin.dll. These are now built from the local source repos and committed
# as static assets rather than fetched from NuGet at runtime by the
# DependencyResolver.
#
# This script:
#   1. Builds ReflectorNet.csproj            -> ReflectorNet.dll
#   2. Builds McpPlugin.Common.csproj        -> McpPlugin.Common.dll
#   3. Builds McpPlugin.csproj               -> McpPlugin.dll
#      (all for netstandard2.1 / Release)
#   4. Copies the three DLLs to
#      Unity-MCP-Plugin/Assets/Plugins/NuGet/
#      preserving existing .meta files (Unity GUIDs / import settings).
#   5. Reports the deployed file size of each DLL.
#
# Usage:
#   ./commands/build-framework-dlls.sh [Configuration] [--skip-build]
#
# Examples:
#   ./commands/build-framework-dlls.sh
#   ./commands/build-framework-dlls.sh Debug
#   ./commands/build-framework-dlls.sh Release --skip-build

set -euo pipefail

# --- Arguments ---------------------------------------------------------------
CONFIGURATION="Release"
SKIP_BUILD=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        Debug|Release)
            CONFIGURATION="$1"
            shift
            ;;
        --skip-build|-s)
            SKIP_BUILD=true
            shift
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

FRAMEWORK="netstandard2.1"

# --- Path resolution ---------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNITY_MCP_DIR="$(dirname "$SCRIPT_DIR")"          # .../Unity-MCP
WORKSPACE="$(dirname "$UNITY_MCP_DIR")"            # .../unity-copilot

REFLECTOR_NET_DIR="$UNITY_MCP_DIR/ReflectorNet"
MCP_PLUGIN_DIR="$UNITY_MCP_DIR/McpPlugin"

DST_DIR="$UNITY_MCP_DIR/Unity-MCP-Plugin/Assets/Plugins/NuGet"

# Source project files (build order matters: ReflectorNet -> Common -> McpPlugin)
declare -a BUILD_ORDER=(
    "ReflectorNet|$REFLECTOR_NET_DIR/ReflectorNet/ReflectorNet.csproj"
    "McpPlugin.Common|$MCP_PLUGIN_DIR/McpPlugin.Common/McpPlugin.Common.csproj"
    "McpPlugin|$MCP_PLUGIN_DIR/McpPlugin/McpPlugin.csproj"
)

# Build output -> deployed DLL name mapping
declare -A SRC_DLL=(
    ["ReflectorNet.dll"]="$REFLECTOR_NET_DIR/ReflectorNet/bin/$CONFIGURATION/$FRAMEWORK/ReflectorNet.dll"
    ["McpPlugin.Common.dll"]="$MCP_PLUGIN_DIR/McpPlugin.Common/bin/$CONFIGURATION/$FRAMEWORK/McpPlugin.Common.dll"
    ["McpPlugin.dll"]="$MCP_PLUGIN_DIR/McpPlugin/bin/$CONFIGURATION/$FRAMEWORK/McpPlugin.dll"
)

# --- Validate source paths ---------------------------------------------------
if [[ ! -d "$REFLECTOR_NET_DIR" ]]; then
    echo "ERROR: ReflectorNet source not found at: $REFLECTOR_NET_DIR" >&2
    echo "       Expected as a subdirectory of Unity-MCP." >&2
    exit 1
fi
if [[ ! -d "$MCP_PLUGIN_DIR" ]]; then
    echo "ERROR: McpPlugin source not found at: $MCP_PLUGIN_DIR" >&2
    echo "       Expected as a subdirectory of Unity-MCP." >&2
    exit 1
fi
if [[ ! -d "$DST_DIR" ]]; then
    echo "ERROR: Destination not found: $DST_DIR" >&2
    echo "       Is Unity-MCP-Plugin checked out?" >&2
    exit 1
fi

# --- Locate dotnet -----------------------------------------------------------
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: dotnet CLI not found on PATH." >&2
    exit 1
fi
DOTNET="$(command -v dotnet)"

# --- Build -------------------------------------------------------------------
if [[ "$SKIP_BUILD" != "true" ]]; then
    echo "dotnet: $DOTNET"
    echo "Configuration: $CONFIGURATION / $FRAMEWORK"
    echo ""

    for entry in "${BUILD_ORDER[@]}"; do
        LABEL="${entry%%|*}"
        PROJ="${entry#*|}"

        if [[ ! -f "$PROJ" ]]; then
            echo "ERROR: Project file missing: $PROJ" >&2
            exit 1
        fi

        echo "==> Building $LABEL"
        echo "    $PROJ"
        if ! "$DOTNET" build "$PROJ" \
            -c "$CONFIGURATION" -f "$FRAMEWORK" \
            -p:GeneratePackageOnBuild=false \
            -p:TargetFrameworks="$FRAMEWORK" \
            -v minimal; then
            echo "ERROR: dotnet build FAILED for $LABEL." >&2
            echo "       No DLLs were copied to the destination." >&2
            exit 1
        fi
        echo "    OK"
        echo ""
    done
fi

# --- Verify build output exists ----------------------------------------------
MISSING=()
for dll_name in "${!SRC_DLL[@]}"; do
    if [[ ! -f "${SRC_DLL[$dll_name]}" ]]; then
        MISSING+=("${SRC_DLL[$dll_name]}")
    fi
done
if [[ ${#MISSING[@]} -gt 0 ]]; then
    echo "ERROR: Expected build output missing:" >&2
    for m in "${MISSING[@]}"; do
        echo "       $m" >&2
    done
    echo "       Run without --skip-build to compile the projects first." >&2
    exit 1
fi

# --- Copy DLLs (preserve .meta files) ----------------------------------------
echo "==> Deploying DLLs to $DST_DIR"
for dll_name in "${!SRC_DLL[@]}"; do
    src="${SRC_DLL[$dll_name]}"
    dst="$DST_DIR/$dll_name"

    cp -f "$src" "$dst"
    size=$(stat -f%z "$dst" 2>/dev/null || stat -c%s "$dst" 2>/dev/null)
    printf "    %-25s %10s bytes\n" "$dll_name" "$size"

    # Warn if .meta is missing — Unity requires it.
    meta_path="$dst.meta"
    if [[ ! -f "$meta_path" ]]; then
        echo "    WARNING: .meta file missing for $dll_name"
        echo "             Unity will generate one on next import (GUID will change)."
    fi
done

echo ""
echo "Done."
echo "Reopen Unity Editor (or trigger Reimport) to load the refreshed plugin DLLs."
