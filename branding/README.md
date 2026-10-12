# Duplicates logo

`Duplicates.png` is the original 1280 px master generated with the built-in imagegen tool. The enlarged warm-white and red document shapes have a fully transparent background, including the gap between them. There is no background plate.

Regenerate the app assets on Windows with `pwsh -File tools/Generate-AppIcons.ps1`. This performs image sizing and ICO packaging without regenerating the design.

- ICO and PNG target sizes: 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128 and 256 px.
- Each target size has default, unplated and light-unplated variants. All three variants preserve the transparent background and the same two document shapes.
- Square44, Square150, Wide310, splash and Store assets cover 100%, 125%, 150%, 200%, 250%, 300% and 400% display scales.
- The ICO is embedded in the executable and used by `AppWindow.SetIcon`. The title bar uses the matching PNG. MSIX resolves the existing manifest paths to their scaled variants.

Size reference: [Microsoft's Windows app icon construction guide](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction).

## Final image edit prompt

Edit this app icon. Remove the entire black rounded-square background plate COMPLETELY. Remove ALL dark/black pixels including the dark separation gap between the two document silhouettes, making those areas truly transparent alpha. The final image must consist ONLY of the existing warm-white rear file silhouette and solid red front file silhouette, floating on fully transparent background, with transparent negative space between them. Preserve the existing shapes' clipped corners, proportions, colors and diagonal overlapping arrangement exactly. No square container, no badge, no black backdrop, no black outline, no shadows, no new elements. The overall exterior silhouette must follow only the white and red document shapes. Enlarge and optically center this two-document mark so its combined bounding box occupies roughly 90% of the canvas width/height for excellent taskbar visibility. Flatten fills to solid warm white #F5EDED and red #D72323, no texture or shading. Produce one square high-resolution transparent PNG icon, no text, no checkerboard baked into image.
