using NexusMods.Abstractions.Library;

namespace NexusMods.App.UI.Overlays;

public class LocalFileMetadataOverlayDesignViewModel() : LocalFileMetadataOverlayViewModel(
    title: "Agregar archivo",
    acceptText: "Agregar",
    originPath: "/home/usuario/Descargas/Better Minimap-1.2.3.zip",
    initial: new LocalFileMetadata(Name: "Better Minimap", Version: "1.2.3", Source: "GitHub", PageUri: new Uri("https://github.com/x/better-minimap")));
