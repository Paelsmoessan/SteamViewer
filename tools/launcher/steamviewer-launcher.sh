#!/bin/bash
# SteamViewer Launcher - Downloads and runs the server for macOS/Linux
# Usage: curl -sSL https://github.com/USER/SteamViewer.NET/releases/latest/download/steamviewer-launcher.sh | bash

set -e

REPO_OWNER="Jeyloh"  # TODO: Update to your GitHub username
REPO_NAME="SteamViewer.NET"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

log_status() {
    echo -e "${CYAN}[SteamViewer]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[OK]${NC} $1"
}

# Detect platform and architecture
detect_platform() {
    case "$(uname -s)" in
        Darwin)
            PLATFORM="osx"
            APP_DIR="$HOME/Library/Application Support/SteamViewer"
            case "$(uname -m)" in
                arm64) ARCH="arm64" ;;
                x86_64) ARCH="x64" ;;
                *)
                    log_error "Unsupported macOS architecture: $(uname -m)"
                    exit 1
                    ;;
            esac
            ;;
        Linux)
            PLATFORM="linux"
            APP_DIR="$HOME/.local/share/SteamViewer"
            case "$(uname -m)" in
                x86_64) ARCH="x64" ;;
                aarch64) ARCH="arm64" ;;
                *)
                    log_error "Unsupported Linux architecture: $(uname -m)"
                    exit 1
                    ;;
            esac
            ;;
        MINGW*|CYGWIN*|MSYS*)
            log_error "Windows detected. Please use SteamViewer.Launcher.exe instead."
            exit 1
            ;;
        *)
            log_error "Unsupported platform: $(uname -s)"
            exit 1
            ;;
    esac

    SERVER_NAME="SteamViewer.Server-${PLATFORM}-${ARCH}"
    SERVER_PATH="$APP_DIR/$SERVER_NAME"
    VERSION_FILE="$APP_DIR/version.txt"

    log_status "Platform: $PLATFORM-$ARCH"
}

# Get latest release from GitHub API
get_latest_release() {
    curl -s \
        -H "Accept: application/vnd.github.v3+json" \
        -H "User-Agent: SteamViewer-Launcher" \
        "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest"
}

# Extract JSON value (simple parser, no jq dependency)
json_value() {
    local key=$1
    local json=$2
    echo "$json" | grep -o "\"$key\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -1 | cut -d'"' -f4
}

# Download with progress
download_file() {
    local url=$1
    local output=$2

    if command -v curl &> /dev/null; then
        curl -L --progress-bar -H "User-Agent: SteamViewer-Launcher" -o "$output" "$url"
    elif command -v wget &> /dev/null; then
        wget --show-progress -q -O "$output" "$url"
    else
        log_error "Neither curl nor wget found. Please install one of them."
        exit 1
    fi
}

# Main execution
main() {
    echo ""
    echo "========================================"
    echo "       SteamViewer Launcher"
    echo "========================================"
    echo ""

    detect_platform

    needs_download=false

    # Check if server exists
    if [ ! -f "$SERVER_PATH" ]; then
        log_status "Server not found, downloading..."
        needs_download=true
    else
        # Check for updates
        log_status "Checking for updates..."
        release=$(get_latest_release)
        latest_version=$(json_value "tag_name" "$release")

        if [ -f "$VERSION_FILE" ]; then
            current_version=$(cat "$VERSION_FILE" | tr -d '[:space:]')

            if [ "$current_version" != "$latest_version" ]; then
                log_status "Update available: $current_version -> $latest_version"
                needs_download=true
            else
                log_success "Already up to date ($current_version)"
            fi
        else
            log_status "Version file missing, downloading latest..."
            needs_download=true
        fi
    fi

    if [ "$needs_download" = true ]; then
        # Get release info if we don't have it
        if [ -z "$release" ]; then
            release=$(get_latest_release)
        fi

        tag=$(json_value "tag_name" "$release")

        if [ -z "$tag" ]; then
            log_error "Could not fetch release information. Check your internet connection."
            exit 1
        fi

        # Create app directory
        mkdir -p "$APP_DIR"

        # Find download URL for our platform
        download_url=$(echo "$release" | grep -o "\"browser_download_url\"[[:space:]]*:[[:space:]]*\"[^\"]*${SERVER_NAME}[^\"]*\"" | head -1 | cut -d'"' -f4)

        if [ -z "$download_url" ]; then
            log_error "Could not find $SERVER_NAME in release $tag"
            log_error "This platform may not be supported yet."
            exit 1
        fi

        log_status "Downloading SteamViewer Server $tag..."
        log_status "URL: $download_url"

        temp_file="$APP_DIR/${SERVER_NAME}.tmp"
        download_file "$download_url" "$temp_file"

        # Verify download
        if [ -f "$temp_file" ]; then
            file_size=$(stat -f%z "$temp_file" 2>/dev/null || stat -c%s "$temp_file" 2>/dev/null || echo "0")

            if [ "$file_size" -gt 1000000 ]; then
                # Move to final location
                mv "$temp_file" "$SERVER_PATH"
                chmod +x "$SERVER_PATH"

                # Save version
                echo "$tag" > "$VERSION_FILE"

                size_mb=$(echo "scale=2; $file_size / 1048576" | bc 2>/dev/null || echo "?")
                log_success "Download complete! (${size_mb} MB)"
            else
                log_error "Downloaded file is too small ($file_size bytes), may be corrupt"
                rm -f "$temp_file"
                exit 1
            fi
        else
            log_error "Download failed"
            exit 1
        fi
    fi

    # Run the server
    if [ -f "$SERVER_PATH" ]; then
        echo ""
        log_status "Starting SteamViewer Server..."
        echo ""

        exec "$SERVER_PATH"
    else
        log_error "Server executable not found at: $SERVER_PATH"
        exit 1
    fi
}

# Run main function
main "$@"
