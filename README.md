# tModManager

Gestor de mods para **Cyberpunk 2077** en **Linux** vía **Steam (Proton)**. Nace como fork de [NexusMods.App](https://github.com/Nexus-Mods/NexusMods.App) (GPL-3.0), discontinuado por Nexus Mods; el código base y la licencia se heredan de ahí, el desarrollo sigue acá con foco en Linux. Ver [TODO.md](TODO.md) para el roadmap (incluye soporte multi-juego).

## 🎯 Objetivo

Gestor de mods nativo para Linux que resuelve las complejidades de instalar mods de Cyberpunk 2077 bajo Proton/Wine. Prioriza compatibilidad con el sistema de archivos de Linux, integración con Steam Deck y escritorios Linux.

## ✨ Features del Fork

### Descarga sin Premium

Usuarios free y supporter pueden descargar todos los mods de una colección con un solo click. El sistema lee las cookies de sesión de Firefox y usa `curl` como subprocess para bypassear el TLS fingerprinting de Cloudflare. Ver [Cómo funciona la descarga directa](#-cómo-funciona-la-descarga-directa).

### Deep Clean y Storage Manager

Herramienta integrada en la página Storage Manager que:

- Mueve carpetas de mods (`red4ext`, `r6/scripts`, `r6/tweaks`, `bin/x64/plugins`, `archive/pc/mod`, etc.) a `~/.local/share/tModManager/Backups/<timestamp>/`
- Elimina DLLs inyectadas (`d3d11.dll`, `winmm.dll`, `version.dll`, `powrprof.dll`)
- Borra backups anteriores para evitar acumulación (conserva el nuevo y el anterior)
- Elimina grupos de mods/colecciones de la DB (conserva archivos de librería para reinstalar sin re-descargar)
- Re-escanea el directorio del juego

El Storage Manager también permite limpiar el store interno (`tModManager/DataModel/Archives`, se reconstruye desde Descargas), las descargas y los backups físicos de forma independiente. Si quedan datos del formato `.nx` anterior, un asistente guía la limpieza.

### Aislamiento del Sistema

- **App ID:** `io.github.t4toh.tmodmanager` (convive con la versión oficial)
- **Datos:** `~/.local/share/tModManager/` (DB y configuración independientes). Instalaciones previas en `NexusMods.App.Cyberpunk/` se migran solas al primer arranque
- **Descargas propias:** Los archivos originales se guardan en `~/.local/share/tModManager/Downloads`; las descargas de la versión oficial se pueden mover ahí desde el asistente de limpieza
- **Protocolo NXM:** Handler independiente para captura de enlaces `nxm://`

### Sin Telemetría

Eliminados completamente Matomo, Mixpanel y OpenTelemetry. La app no envía ningún dato a servidores externos.

### Diagnósticos de Mods Esenciales

Sistema de diagnósticos que verifica la presencia de mods base críticos para Cyberpunk 2077:

- **Redscript** — Compilador de scripts
- **RED4ext** — Mod loader para REDengine 4
- **Cyber Engine Tweaks (CET)** — Framework de scripting
- **ArchiveXL / TweakXL** — Plugins para recursos custom y TweakDB
- **Codeware** — Librería de utilidades
- **Equipment-EX** — Sistema de transmog

Detecta también carpetas redundantes en mods (ej. `Cyberpunk 2077/bin/...` duplicado) y dependencias entre mods por patrones de archivo.

### Soporte Wine/Proton Avanzado

- **Detección manual de juego:** Permite especificar la ruta de instalación y el prefijo WINE/Proton manualmente
- **Soporte Lutris:** Parsea configs YAML de Lutris para detectar DLL overrides (`WINEDLLOVERRIDES`)
- **Diagnóstico de Wine prefix:** Emitter que verifica requerimientos del prefix (protontricks, REDmod)
- **Winetricks:** Parsea `winetricks.log` del prefix para detectar paquetes instalados

### Colecciones Mejoradas

- **Navegación global:** Explorar y descargar colecciones sin tener un juego gestionado
- **Pestaña "Mod List":** Detalle de cada mod en la colección (hashes, enlaces, copiar al portapapeles)
- **Rescan de descargas:** Detección por MD5 de archivos ya descargados para evitar re-descargas
- **Resiliencia:** Si el archivo de colección se borra del disco, se re-descarga automáticamente; la página carga sin crashear; Apply funciona con instalaciones parciales
- **Validación de archivos:** Si faltan archivos en el store (tras Deep Clean, GC o un borrado manual) se reextraen desde la descarga original; solo si esa descarga ya no está se muestra el botón de descarga. Valida todos los hijos del archivo, no solo el primero
- **Fallback por ruta:** Si el MD5 de un archivo no coincide (mod actualizado), intenta mapeo por ruta relativa antes de fallar
- **Advertencia de conflictos:** Avisa antes de instalar una colección si ya hay otra instalada

### Robustez de Descargas

- **Archivos vacíos:** Si una descarga produce 0 bytes, error antes de crear entrada en librería
- **Magic bytes:** Cuando el servidor no envía extensión, detecta el tipo real (7z/zip/rar) leyendo los headers
- **Extensiones de backup:** Los backups mantienen sus extensiones originales

### Otras Mejoras

- **Foco único:** Eliminado soporte para GOG, EGS, Windows, macOS y otros juegos
- **Rutas XDG nativas:** Uso correcto de `XDG_DATA_HOME`
- **Grupo "My Mods" automático:** Se crea automáticamente al instalar un mod si no existe un grupo mutable

## 🛠 Arquitectura

| Componente         | Tecnología                            |
| ------------------ | ------------------------------------- |
| **Lenguaje**       | C# / .NET 10                          |
| **UI**             | Avalonia UI (MVVM con R3/ReactiveUI)  |
| **Base de Datos**  | MnemonicDB (inmutable, EAV)           |
| **Sincronización** | Three-way diff con symlinks/hardlinks |
| **Empaquetado**    | AppImage vía PupNet                   |

### Estructura de la Solución (81 proyectos)

| Proyecto(s)                           | Propósito                                                         |
| ------------------------------------- | ----------------------------------------------------------------- |
| `NexusMods.App`                       | Entry point; DI, Avalonia UI o CLI                                |
| `NexusMods.App.UI`                    | Vistas Avalonia + ViewModels (MVVM)                               |
| `NexusMods.App.Cli`                   | Comandos CLI                                                      |
| `NexusMods.Backend`                   | Interop Linux, extracción, localizadores de juego (Steam, manual) |
| `NexusMods.DataModel`                 | Persistencia MnemonicDB, sincronizador, Storage Manager           |
| `NexusMods.Library`                   | Librería de mods (add/remove/install)                             |
| `NexusMods.Collections`               | Descarga e instalación de colecciones de Nexus Mods               |
| `NexusMods.Sdk`                       | Utilidades compartidas, WineParser, settings                      |
| `NexusMods.Abstractions.*`            | Interfaces/contratos entre subsistemas (21 proyectos)             |
| `NexusMods.Games.RedEngine`           | Implementación de Cyberpunk 2077 (único juego soportado)          |
| `NexusMods.Games.FileHashes`          | DB de hashes para detección de versión (Steam)                    |
| `NexusMods.Networking.Steam`          | Integración con Steam                                             |
| `NexusMods.Networking.NexusWebApi`    | API de Nexus Mods + FirefoxCookieReader                           |
| `NexusMods.Networking.HttpDownloader` | Infraestructura de descarga HTTP                                  |

## 🚀 Roadmap

Pendientes, deuda técnica y estado de errores en [TODO.md](TODO.md).

## 🏗 Cómo Construir

```bash
dotnet build
dotnet run --project src/NexusMods.App/NexusMods.App.csproj
```

Script interactivo con menú en español:

```bash
./dev.sh
```

Opciones disponibles: compilar, ejecutar, tests (todos / sin red / RedEngine / específico), limpiar, restaurar, pipeline completo, generar AppImage.

Para generar una AppImage (requiere `pupnet`):

```bash
# Desde dev.sh opción 10, o manualmente:
pupnet -r linux-x64 -k appimage
```

## 🔐 Cómo Funciona la Descarga Directa

Este sistema permite descargar mods de Nexus Mods sin la API premium, usando la sesión de tu browser.

### El problema: TLS Fingerprinting

Nexus Mods usa Cloudflare, que detecta clientes no-browser mediante **TLS fingerprinting** (análisis del handshake TLS). El `HttpClient` de .NET tiene un fingerprint diferente al de Firefox/Chrome, por lo que Cloudflare responde con una URL vacía (`https://cf-files.nexusmods.com/cdn///`) en vez del link de descarga real.

**Solución:** invocar `curl` como subprocess. Curl tiene un fingerprint aceptado por Cloudflare y devuelve la URL CDN real.

### Flujo completo

```
1. Colección JSON → lista de FileId + GameId por cada mod
2. Para cada mod (serializado, máx. 1 curl a la vez):
   a. Leer cookies de sesión del perfil Firefox (~/.mozilla/firefox o ~/.config/mozilla/firefox)
   b. curl -X POST https://www.nexusmods.com/Core/Libs/Common/Managers/Downloads?GenerateDownloadUrl
         -H "Cookie: <session cookies>"
         -H "User-Agent: Mozilla/5.0 (Firefox)"
         --data "fid=<fileId>&game_id=<gameId>"
   c. Respuesta JSON:
      - Free/Supporter: {"url": "https://files.nexus-cdn.com/<path>?md5=...&expires=..."}
      - Premium:        [{"name":"CDN Name","URI":"https://..."}]
3. Descargar el archivo desde la URL CDN con el HttpDownloadJob existente
4. Máx. 5 descargas en paralelo (configurable en Settings → Downloads)
```

### Lectura de cookies del browser

El archivo `FirefoxCookieReader.cs` hace:

1. Busca `profiles.ini` en `~/.mozilla/firefox` y `~/.config/mozilla/firefox`
2. Resuelve el perfil por defecto (primero la entrada `[Install...]Default=`, luego el primer `[Profile...]`)
3. Copia `cookies.sqlite` a un archivo temporal (para evitar el lock de SQLite cuando Firefox está abierto)
4. Lee todas las cookies con `host LIKE '%nexusmods.com%'` via P/Invoke a `libsqlite3.so.0`

### Agregar soporte para otro browser

Para soportar Chrome/Chromium (o cualquier browser basado en Chromium) hay que tener en cuenta dos diferencias:

**1. Ubicación de la base de datos**

```
Chrome:    ~/.config/google-chrome/Default/Cookies
Chromium:  ~/.config/chromium/Default/Cookies
Brave:     ~/.config/BraveSoftware/Brave-Browser/Default/Cookies
```

**2. Cookies encriptadas (DPAPI en Linux = Secret Service)**

A diferencia de Firefox, Chrome/Chromium **encripta los valores** de las cookies usando el sistema de claves del OS:

- En Linux, usa `libsecret` (freedesktop Secret Service) para guardar la clave de encriptación
- La clave se guarda en el keyring con el label `"Chrome Safe Storage"` (o similar)
- Las cookies se encriptan con AES-256-CBC usando esa clave

Para desencriptarlas necesitás:

```csharp
// Pseudocódigo
var key = await SecretServiceClient.GetSecret("Chrome Safe Storage");
var derivedKey = Pbkdf2(key, salt: "saltysalt", iterations: 1, keyLen: 16);
var decryptedValue = AesDecrypt(encryptedCookieValue[3..], key: derivedKey, iv: new byte[16]);
```

La estructura en SQLite es diferente:

```sql
-- Chrome usa la tabla "cookies" (no "moz_cookies")
SELECT name, encrypted_value FROM cookies
WHERE host_key LIKE '%nexusmods.com%'
```

**3. Estructura del archivo `IBrowserCookieReader` a implementar**

```csharp
// src/NexusMods.Networking.NexusWebApi/IBrowserCookieReader.cs
internal interface IBrowserCookieReader
{
    string BrowserName { get; }
    bool IsAvailable();
    string? TryGetNexusModsCookieHeader(ILogger logger);
}
```

Luego en `NexusApiClient.GenerateDirectDownloadUrlAsync`, iterar los readers disponibles en orden de prioridad:

```csharp
var readers = new IBrowserCookieReader[] { new FirefoxCookieReader(), new ChromeCookieReader() };
var cookies = readers.Select(r => r.TryGetNexusModsCookieHeader(_logger)).FirstOrDefault(c => c is not null);
```

### Limitaciones conocidas

- **Solo Firefox en este momento.** Chrome/Chromium requieren desencriptado adicional.
- **Velocidad:** Usuarios free están limitados a ~300-500 KB/s por descarga (límite del servidor, no del cliente). Con 5 descargas paralelas se puede alcanzar ~2.5 MB/s totales.
- **Sesión requerida:** El usuario debe haber iniciado sesión en Nexus Mods dentro del browser. La app no gestiona el login del website.
- **Semáforo en curl:** Las llamadas al endpoint `GenerateDownloadUrl` están serializadas (1 a la vez) para evitar que Cloudflare devuelva páginas de challenge HTML en lugar de JSON. Una vez obtenida la URL CDN, la descarga HTTP es paralela.

---

_Nota: Este proyecto no está afiliado oficialmente con Nexus Mods._
