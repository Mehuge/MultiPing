#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT_DIR/MultiPing.csproj"
CONFIGURATION="Release"
RID=""
APP_VERSION="${APP_VERSION:-1.0.0}"
CODESIGN_IDENTITY="${CODESIGN_IDENTITY:-}"
NOTARIZE=0
NOTARY_PROFILE="${NOTARY_PROFILE:-}"

usage() {
    cat <<'EOF'
Usage: package-app.sh --rid osx-x64|osx-arm64 [options]

Options:
  --configuration DIR       Build configuration (default: Release)
  --version VERSION         Bundle version (default: APP_VERSION or 1.0.0)
  --notarize                Submit the signed ZIP with notarytool
  --notary-profile NAME     Keychain profile used by notarytool
  --help                    Show this help

Environment:
  CODESIGN_IDENTITY         Developer ID Application identity used by codesign
  NOTARY_PROFILE            Alternative way to provide the notarytool profile
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            RID="${2:?--rid requires osx-x64 or osx-arm64}"
            shift 2
            ;;
        --configuration)
            CONFIGURATION="${2:?--configuration requires a value}"
            shift 2
            ;;
        --version)
            APP_VERSION="${2:?--version requires a value}"
            shift 2
            ;;
        --notarize)
            NOTARIZE=1
            shift
            ;;
        --notary-profile)
            NOTARY_PROFILE="${2:?--notary-profile requires a value}"
            shift 2
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

case "$RID" in
    osx-x64|osx-arm64) ;;
    *)
        echo "--rid must be osx-x64 or osx-arm64" >&2
        exit 2
        ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet is required." >&2
    exit 1
fi

PUBLISH_DIR="$ROOT_DIR/bin/$CONFIGURATION/net10.0/$RID/publish"
APP_PATH="$PUBLISH_DIR/MultiPing.app"
ZIP_PATH="$PUBLISH_DIR/MultiPing-$RID.zip"

echo "Publishing $RID ($CONFIGURATION)..."
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/MultiPing" ]]; then
    echo "Publish did not produce the macOS executable: $PUBLISH_DIR/MultiPing" >&2
    exit 1
fi

rm -rf "$APP_PATH"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"
cp "$PUBLISH_DIR/MultiPing" "$APP_PATH/Contents/MacOS/MultiPing"
cp "$ROOT_DIR/packaging/macOS/Info.plist" "$APP_PATH/Contents/Info.plist"
chmod 755 "$APP_PATH/Contents/MacOS/MultiPing"

if command -v /usr/libexec/PlistBuddy >/dev/null 2>&1; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $APP_VERSION" "$APP_PATH/Contents/Info.plist" >/dev/null
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $APP_VERSION" "$APP_PATH/Contents/Info.plist" >/dev/null
    /usr/libexec/PlistBuddy -c "Set :CFBundleGetInfoString MultiPing $APP_VERSION" "$APP_PATH/Contents/Info.plist" >/dev/null
fi

ICONSET="$PUBLISH_DIR/MultiPing.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    for size in 16 32 64 128 256 512 1024; do
        sips -z "$size" "$size" "$ROOT_DIR/Assets/logo.png" \
            --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP_PATH/Contents/Resources/MultiPing.icns"
    if command -v /usr/libexec/PlistBuddy >/dev/null 2>&1; then
        /usr/libexec/PlistBuddy -c "Set :CFBundleIconFile MultiPing.icns" "$APP_PATH/Contents/Info.plist" >/dev/null
    fi
else
    # Keep the source artwork available even when packaging on a non-macOS host.
    cp "$ROOT_DIR/Assets/logo.png" "$APP_PATH/Contents/Resources/logo.png"
fi

if [[ -n "$CODESIGN_IDENTITY" ]]; then
    if ! command -v codesign >/dev/null 2>&1; then
        echo "CODESIGN_IDENTITY was set but codesign is unavailable." >&2
        exit 1
    fi
    echo "Signing $APP_PATH with $CODESIGN_IDENTITY..."
    codesign --force --deep --options runtime \
        --entitlements "$ROOT_DIR/packaging/macOS/MultiPing.entitlements" \
        --timestamp \
        --sign "$CODESIGN_IDENTITY" \
        "$APP_PATH"
    codesign --verify --deep --strict --verbose=2 "$APP_PATH"
fi

if [[ "$NOTARIZE" -eq 1 ]]; then
    if [[ -z "$CODESIGN_IDENTITY" ]]; then
        echo "--notarize requires CODESIGN_IDENTITY to be set." >&2
        exit 2
    fi
    if [[ -z "$NOTARY_PROFILE" ]]; then
        echo "--notarize requires --notary-profile or NOTARY_PROFILE." >&2
        exit 2
    fi
    if ! command -v ditto >/dev/null 2>&1 || ! command -v xcrun >/dev/null 2>&1; then
        echo "ditto and xcrun are required for notarization." >&2
        exit 1
    fi
    rm -f "$ZIP_PATH"
    ditto -c -k --keepParent "$APP_PATH" "$ZIP_PATH"
    echo "Submitting $ZIP_PATH for notarization..."
    xcrun notarytool submit "$ZIP_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP_PATH"
fi

echo "Created: $APP_PATH"
if [[ -f "$ZIP_PATH" ]]; then
    echo "Created: $ZIP_PATH"
fi
