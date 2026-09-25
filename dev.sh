#!/bin/bash

# Script de utilidad para tareas comunes de desarrollo

# Colores para output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# StrawberryShake incluye herramientas net9 que necesitan rollforward a net10
export DOTNET_ROLL_FORWARD=LatestMajor

# Asegurar que dotnet tools globales esten en PATH (y el SDK local en ~/.dotnet si existe)
export PATH="$HOME/.dotnet/tools:$PATH"
if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi
APP_PROJECT="$SCRIPT_DIR/src/NexusMods.App/NexusMods.App.csproj"
APP_DIR="$SCRIPT_DIR/src/NexusMods.App"
REDENGINE_TESTS="$SCRIPT_DIR/tests/Games/NexusMods.Games.RedEngine.Tests"

echo -e "${BLUE}NexusMods.App - Herramientas de Desarrollo${NC}"
echo ""

show_menu() {
    echo "Selecciona una opcion:"
    echo ""
    echo "  1) Compilar solucion"
    echo "  2) Ejecutar app"
    echo "  3) Ejecutar todos los tests"
    echo "  4) Ejecutar tests (sin red/flakey)"
    echo "  5) Ejecutar tests de RedEngine (CP2077)"
    echo "  6) Ejecutar un test especifico"
    echo "  7) Limpiar proyecto"
    echo "  8) Restaurar dependencias"
    echo "  9) Todo (limpiar + restaurar + compilar + tests)"
    echo " 10) Generar AppImage"
    echo "  0) Salir"
    echo ""
    read -p "Opcion: " option
    echo ""
}

build_solution() {
    echo -e "${GREEN}Compilando solucion...${NC}"
    dotnet build "$SCRIPT_DIR"
}

run_app() {
    echo -e "${GREEN}Ejecutando app...${NC}"
    dotnet run --project "$APP_PROJECT"
}

# Un proyecto por vez: `dotnet test` sobre la solucion entera corre todo en paralelo y casi congela la PC.
# Mismo recorrido que tenia el CI: xUnit con `dotnet test`, TUnit (Sdk.Tests, Backend.Tests) con `dotnet run`.
run_tests_sequential() {
    local filter="$1"
    local failed=()
    dotnet build "$SCRIPT_DIR" -p:TreatWarningsAsErrors=true || return 1
    for proj in $(grep -rL '<UsesTUnit>true' --include='*.Tests.csproj' "$SCRIPT_DIR/tests"); do
        echo -e "${BLUE}== $(basename "$proj" .csproj)${NC}"
        if [[ -n "$filter" ]]; then
            dotnet test "$proj" --no-build --filter "$filter" || failed+=("$proj")
        else
            dotnet test "$proj" --no-build || failed+=("$proj")
        fi
    done
    for proj in $(grep -rl '<UsesTUnit>true' --include='*.Tests.csproj' "$SCRIPT_DIR/tests"); do
        echo -e "${BLUE}== $(basename "$proj" .csproj) (TUnit)${NC}"
        dotnet run --project "$proj" --no-build || failed+=("$proj")
    done
    if (( ${#failed[@]} )); then
        echo -e "${RED}Proyectos con fallas:${NC}"
        printf '  %s\n' "${failed[@]}"
        return 1
    fi
    echo -e "${GREEN}Todos los tests pasaron${NC}"
}

run_all_tests() {
    echo -e "${GREEN}Ejecutando todos los tests (proyecto por proyecto)...${NC}"
    run_tests_sequential ""
}

run_safe_tests() {
    echo -e "${GREEN}Ejecutando tests sin red/flakey (proyecto por proyecto)...${NC}"
    run_tests_sequential "RequiresNetworking!=True&FlakeyTest!=True"
}

run_redengine_tests() {
    echo -e "${GREEN}Ejecutando tests de RedEngine (CP2077)...${NC}"
    dotnet test "$REDENGINE_TESTS"
}

run_single_test() {
    read -p "Nombre del test (ej: SomeClass.SomeMethod): " test_name
    if [[ -z "$test_name" ]]; then
        echo -e "${YELLOW}No se especifico ningun test${NC}"
        return
    fi
    echo -e "${GREEN}Ejecutando test: $test_name${NC}"
    dotnet test "$SCRIPT_DIR" --filter "FullyQualifiedName~$test_name"
}

clean_project() {
    echo -e "${GREEN}Limpiando proyecto...${NC}"
    dotnet clean "$SCRIPT_DIR"
    echo -e "${GREEN}Proyecto limpio${NC}"
}

restore_deps() {
    echo -e "${GREEN}Restaurando dependencias...${NC}"
    dotnet restore "$SCRIPT_DIR"
}

run_all() {
    echo -e "${YELLOW}Ejecutando pipeline completo...${NC}"
    echo ""
    clean_project
    echo ""
    restore_deps
    echo ""
    build_solution
    if [[ $? -ne 0 ]]; then
        echo -e "${RED}La compilacion fallo. Abortando.${NC}"
        return
    fi
    echo ""
    run_safe_tests
    echo ""
    echo -e "${GREEN}Pipeline completado${NC}"
}

ensure_appimagetool() {
    if command -v appimagetool &> /dev/null || command -v appimagetool-x86_64.AppImage &> /dev/null; then
        return 0
    fi
    local tool_path="$SCRIPT_DIR/.tools/appimagetool-x86_64.AppImage"
    if [[ -x "$tool_path" ]]; then
        export PATH="$SCRIPT_DIR/.tools:$PATH"
        return 0
    fi
    echo -e "${YELLOW}Descargando appimagetool...${NC}"
    mkdir -p "$SCRIPT_DIR/.tools"
    curl -fSL -o "$tool_path" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    if [[ $? -ne 0 ]]; then
        echo -e "${RED}No se pudo descargar appimagetool${NC}"
        return 1
    fi
    chmod +x "$tool_path"
    export PATH="$SCRIPT_DIR/.tools:$PATH"
    return 0
}

build_appimage() {
    if ! command -v pupnet &> /dev/null; then
        echo -e "${RED}PupNet no esta instalado. Instalar con:${NC}"
        echo "  dotnet tool install --global KuiperZone.PupNet"
        return
    fi
    if ! command -v fusermount &> /dev/null && ! test -f /usr/lib/libfuse.so.2; then
        echo -e "${RED}FUSE no esta instalado. Instalar con:${NC}"
        echo "  sudo pacman -S fuse2"
        return
    fi
    if ! ensure_appimagetool; then
        return
    fi
    echo -e "${GREEN}Generando AppImage...${NC}"
    # Versión desde el último tag (v0.23.4 -> 0.23.4); si no hay tags queda el fallback de app.pupnet.conf
    local app_version
    app_version=$(git -C "$SCRIPT_DIR" describe --tags --abbrev=0 2>/dev/null | sed 's/^v//')
    pushd "$APP_DIR" > /dev/null
    pupnet -y -k AppImage ${app_version:+--app-version "$app_version"} -p DefineConstants=INSTALLATION_METHOD_APPIMAGE
    local exit_code=$?
    popd > /dev/null
    if [[ $exit_code -eq 0 ]]; then
        echo -e "${GREEN}AppImage generado en: $APP_DIR/Deploy/OUT/${NC}"
    else
        echo -e "${RED}Fallo la generacion del AppImage${NC}"
    fi
}

# Loop principal
while true; do
    show_menu

    case $option in
        1) build_solution ;;
        2) run_app ;;
        3) run_all_tests ;;
        4) run_safe_tests ;;
        5) run_redengine_tests ;;
        6) run_single_test ;;
        7) clean_project ;;
        8) restore_deps ;;
        9) run_all ;;
        10) build_appimage ;;
        0)
            echo -e "${BLUE}¡Ahí luego!${NC}"
            exit 0
            ;;
        *)
            echo -e "${YELLOW}Opcion invalida${NC}"
            ;;
    esac

    echo ""
    read -p "Presiona Enter para continuar..."
    clear
done
