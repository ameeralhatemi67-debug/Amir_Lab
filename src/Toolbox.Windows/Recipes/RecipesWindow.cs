using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Core.Presets;
using Toolbox.Core.Recipes;
using Toolbox.Processing.Studio;
using Toolbox.Windows.Studio;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Recipes;

/// <summary>
/// Create, inspect, edit, duplicate, pin, import and export recipes, and manage brand styles.
/// Built-in recipes are read-only (duplicate to change them); they can still be pinned to the wheel.
/// </summary>
internal sealed class RecipesWindow : GlassWindow
{
    private static readonly (RecipeOp Op, string Label)[] StepCatalog =
    [
        (RecipeOp.RenderPdfPages, "PDF pages → images"),
        (RecipeOp.CenterCrop, "Crop to a ratio (centred)"),
        (RecipeOp.Resize, "Resize (longest side)"),
        (RecipeOp.Frame, "Frame / brand style"),
        (RecipeOp.Convert, "Save as format"),
        (RecipeOp.Compress, "Fit under a size"),
        (RecipeOp.RemoveMetadata, "Remove metadata"),
        (RecipeOp.CombineToPdf, "Combine into one PDF"),
    ];

    private readonly ToolboxHost _host;
    private readonly ListBox _recipeList = new();
    private readonly ContentControl _editor = new();
    private readonly ListBox _brandList = new();
    private readonly ContentControl _brandEditor = new();
    private Recipe? _recipe;
    private BrandPreset? _brand;

    public RecipesWindow(ToolboxHost host)
    {
        _host = host;
        Title = "File Toolbox recipes";
        Width = 940;
        Height = 700;
        MinWidth = 760;
        MinHeight = 480;
        Resizable = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();
        Loaded += (_, _) => { FillRecipes(); FillBrands(); };
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = StudioWindow.Res("Toolbox.Button.Icon"), Content = StudioWindow.IconPath("close", 12), ToolTip = "Close" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Recipes", Style = StudioWindow.Res("Text.Title"), FontSize = 16 });
        titles.Children.Add(new TextBlock { Text = "Saved steps you can run on any file from the wheel (third ring), the File Toolbox window or Explorer.", Style = StudioWindow.Res("Text.Caption") });
        header.Children.Add(titles);
        MakeDragHandle(header);
        root.Children.Add(header);

        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Recipes", Content = Split(_recipeList, _editor, RecipeListButtons()) });
        tabs.Items.Add(new TabItem { Header = "Brand styles", Content = Split(_brandList, _brandEditor, BrandListButtons()) });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        _recipeList.SelectionChanged += (_, _) =>
        {
            if (_recipeList.SelectedItem is ListBoxItem { Tag: Recipe r }) ShowRecipe(r);
        };
        _brandList.SelectionChanged += (_, _) =>
        {
            if (_brandList.SelectedItem is ListBoxItem { Tag: BrandPreset b }) ShowBrand(b);
        };

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private static Grid Split(ListBox list, ContentControl editor, UIElement buttons)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(list);
        grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    // ---- recipes ------------------------------------------------------------------------------

    private UIElement RecipeListButtons()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Button("New", () =>
        {
            var recipe = new Recipe { Name = "New recipe", Steps = [new(RecipeOp.Resize, new() { ["maxEdge"] = "1600" }), new(RecipeOp.Convert, new() { ["format"] = "jpg", ["quality"] = "85" })], Suffix = " Edited" };
            _host.Recipes.Save(recipe);
            FillRecipes(recipe.Id);
        }));
        panel.Children.Add(Button("Import…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Recipe files|*.json|All files|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var imported = _host.Recipes.Import(dialog.FileName);
                FillRecipes(imported.Id);
            }
            catch (Exception ex) when (ex is ProcessingException or System.Text.Json.JsonException or IOException)
            {
                MessageBox.Show(this, "That file could not be imported: " + ex.Message, "File Toolbox");
            }
        }));
        return panel;
    }

    private void FillRecipes(string? select = null)
    {
        _recipeList.Items.Clear();
        foreach (var recipe in _host.Recipes.All())
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = recipe.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = (recipe.BuiltIn ? "Built-in" : "Yours") + (recipe.Pinned ? " · on the wheel" : "") + (recipe.Interactive ? " · opens editor" : ""),
                Style = StudioWindow.Res("Text.Caption"),
            });
            var item = new ListBoxItem { Content = stack, Tag = recipe };
            _recipeList.Items.Add(item);
            if (recipe.Id == (select ?? _recipe?.Id)) _recipeList.SelectedItem = item;
        }
        if (_recipeList.SelectedItem is null && _recipeList.Items.Count > 0) _recipeList.SelectedIndex = 0;
    }

    private void ShowRecipe(Recipe recipe)
    {
        _recipe = recipe;
        bool readOnly = recipe.BuiltIn;
        var panel = new StackPanel();

        var name = new TextBox { Text = recipe.Name, IsReadOnly = readOnly };
        var label = new TextBox { Text = recipe.Label, IsReadOnly = readOnly, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Short label shown on the wheel" };
        var description = new TextBox { Text = recipe.Description, IsReadOnly = readOnly, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 44 };
        var suffix = new TextBox { Text = recipe.Suffix, IsReadOnly = readOnly, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Field("Name", name));
        panel.Children.Add(Field("Wheel label", label));
        panel.Children.Add(Field("Description", description));

        var interactive = new CheckBox { Content = "Open the editor first (crop and mark up by hand), then apply the steps", IsChecked = recipe.Interactive, IsEnabled = !readOnly, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(interactive);

        panel.Children.Add(Heading("Steps"));
        var steps = recipe.Steps.ToList();
        var stepsHost = new StackPanel();
        var summary = new TextBlock { Style = StudioWindow.Res("Text.Caption"), TextWrapping = TextWrapping.Wrap };
        Recipe Current() => recipe with
        {
            Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : recipe.Name,
            Label = label.Text.Trim(),
            Description = description.Text.Trim(),
            Suffix = suffix.Text,
            Interactive = interactive.IsChecked == true,
            Steps = steps.ToList(),
        };
        void RenderSteps()
        {
            stepsHost.Children.Clear();
            for (int i = 0; i < steps.Count; i++) stepsHost.Children.Add(StepCard(steps, i, readOnly, RenderSteps));
            summary.Text = Current().WillProduce();
        }
        RenderSteps();
        panel.Children.Add(stepsHost);
        if (!readOnly)
        {
            var add = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            add.Items.Add("+ Add a step…");
            foreach (var (_, text) in StepCatalog) add.Items.Add(text);
            add.SelectedIndex = 0;
            add.SelectionChanged += (_, _) =>
            {
                if (add.SelectedIndex <= 0) return;
                steps.Add(new RecipeStep(StepCatalog[add.SelectedIndex - 1].Op, DefaultSettings(StepCatalog[add.SelectedIndex - 1].Op)));
                add.SelectedIndex = 0;
                RenderSteps();
            };
            panel.Children.Add(add);
        }

        panel.Children.Add(Heading("Results"));
        panel.Children.Add(Field("Name suffix", suffix, "Added to each original name, e.g. \"photo Web.webp\"."));
        var destination = new TextBox { Text = recipe.Destination ?? "", IsReadOnly = true, ToolTip = "Empty: next to each original" };
        var pickDest = Button("Folder…", () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) destination.Text = dialog.SelectedPath;
        });
        var clearDest = Button("Next to original", () => destination.Text = "");
        pickDest.IsEnabled = clearDest.IsEnabled = !readOnly;
        var destRow = new DockPanel();
        var destButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        destButtons.Children.Add(pickDest);
        destButtons.Children.Add(clearDest);
        DockPanel.SetDock(destButtons, Dock.Right);
        destRow.Children.Add(destButtons);
        destRow.Children.Add(destination);
        panel.Children.Add(Field("Save in", destRow));

        var maxSize = new TextBox { Text = recipe.Requirements.MaxBytes is { } mb ? (mb / 1024.0).ToString("0", CultureInfo.InvariantCulture) : "", Width = 90, IsReadOnly = readOnly, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Field("Check: at most (KB)", maxSize, "Each result is checked and reported as met or not met. Empty: no size check."));

        panel.Children.Add(Heading("What it will do"));
        var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Child = summary };
        panel.Children.Add(card);
        foreach (var box in new[] { name, label, description, suffix }) box.TextChanged += (_, _) => summary.Text = Current().WillProduce();
        interactive.Checked += (_, _) => summary.Text = Current().WillProduce();
        interactive.Unchecked += (_, _) => summary.Text = Current().WillProduce();

        var pin = new CheckBox { Content = "Show on the wheel (Recipes ring)", IsChecked = recipe.Pinned, Margin = new Thickness(0, 12, 0, 0) };
        pin.Checked += (_, _) => { _host.Recipes.Save((_recipe ?? recipe) with { Pinned = true }); FillRecipes(); };
        pin.Unchecked += (_, _) => { _host.Recipes.Save((_recipe ?? recipe) with { Pinned = false }); FillRecipes(); };
        panel.Children.Add(pin);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (!readOnly)
        {
            var save = Button("Save", () =>
            {
                long? bytes = double.TryParse(maxSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) && kb > 0 ? (long)(kb * 1024) : null;
                var updated = Current() with
                {
                    Destination = string.IsNullOrWhiteSpace(destination.Text) ? null : destination.Text,
                    Requirements = recipe.Requirements with { MaxBytes = bytes },
                    Pinned = pin.IsChecked == true,
                };
                _host.Recipes.Save(updated);
                _recipe = updated;
                FillRecipes(updated.Id);
            });
            save.Style = StudioWindow.Res("Toolbox.Button.Accent");
            actions.Children.Add(save);
        }
        actions.Children.Add(Button("Duplicate", () => FillRecipes(_host.Recipes.Duplicate(Current()).Id)));
        actions.Children.Add(Button("Export…", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = OutputNamerSafe(recipe.Name) + ".recipe.json", Filter = "Recipe files|*.json" };
            if (dialog.ShowDialog(this) == true) RecipeStore.Export(Current(), dialog.FileName);
        }));
        if (!readOnly)
            actions.Children.Add(Button("Delete", () => { _host.Recipes.Delete(recipe.Id); _recipe = null; FillRecipes(); }));
        panel.Children.Add(actions);
        if (readOnly) panel.Children.Add(Caption("Built-in recipes cannot be changed. Duplicate one to make your own version."));
        _editor.Content = panel;
    }

    private UIElement StepCard(List<RecipeStep> steps, int index, bool readOnly, Action changed)
    {
        var step = steps[index];
        var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 8, 10, 8) };
        var stack = new StackPanel();
        var head = new DockPanel();
        if (!readOnly)
        {
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(Small("↑", index > 0, () => { (steps[index - 1], steps[index]) = (steps[index], steps[index - 1]); changed(); }));
            tools.Children.Add(Small("↓", index < steps.Count - 1, () => { (steps[index + 1], steps[index]) = (steps[index], steps[index + 1]); changed(); }));
            tools.Children.Add(Small("✕", true, () => { steps.RemoveAt(index); changed(); }));
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
        }
        head.Children.Add(new TextBlock { Text = $"{index + 1}. {StepCatalog.First(s => s.Op == step.Op).Label}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = step.Describe(), Style = StudioWindow.Res("Text.Caption") });
        if (!readOnly)
        {
            var settings = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (key, label, choices) in SettingsFor(step.Op))
            {
                var current = step.Get(key, DefaultSettings(step.Op).GetValueOrDefault(key, ""));
                FrameworkElement editor;
                if (choices is not null)
                {
                    var combo = new ComboBox { MinWidth = 110 };
                    foreach (var c in choices) combo.Items.Add(c);
                    combo.SelectedItem = choices.Contains(current) ? current : choices[0];
                    combo.SelectionChanged += (_, _) => { Set(steps, index, key, (string)combo.SelectedItem); changed(); };
                    editor = combo;
                }
                else
                {
                    var box = new TextBox { Text = current, MinWidth = 90 };
                    box.LostKeyboardFocus += (_, _) => { if (box.Text != current) { Set(steps, index, key, box.Text.Trim()); changed(); } };
                    editor = box;
                }
                var pair = new StackPanel { Margin = new Thickness(0, 0, 10, 4) };
                pair.Children.Add(new TextBlock { Text = label, Style = StudioWindow.Res("Text.Label") });
                pair.Children.Add(editor);
                settings.Children.Add(pair);
            }
            stack.Children.Add(settings);
        }
        card.Child = stack;
        return card;
    }

    private static void Set(List<RecipeStep> steps, int index, string key, string value)
    {
        var settings = new Dictionary<string, string>(steps[index].Settings ?? []) { [key] = value };
        steps[index] = steps[index] with { Settings = settings };
    }

    private IEnumerable<(string Key, string Label, string[]? Choices)> SettingsFor(RecipeOp op)
    {
        var brands = new[] { "" }.Concat(_host.Brands.All().Select(b => b.Name)).ToArray();
        return op switch
        {
            RecipeOp.RenderPdfPages => [("pages", "Pages", null), ("dpi", "DPI", ["96", "150", "200", "300"])],
            RecipeOp.CenterCrop => [("ratio", "Ratio", ["1:1", "4:5", "16:9", "9:16", "4:3", "3:4", "3:2"])],
            RecipeOp.Resize => [("maxEdge", "Longest side (px)", null)],
            RecipeOp.Frame =>
            [
                ("brand", "Brand style", brands),
                ("fill", "Background", BackgroundPresets.Gradients.Select(g => "preset:" + g.Id).Concat(BackgroundPresets.Solids).ToArray()),
                ("padding", "Padding (px)", null), ("corner", "Corners (px)", null), ("shadow", "Shadow (px)", null),
                ("ratio", "Shape", ["auto", "1:1", "4:5", "16:9", "9:16"]),
                ("ratios", "Also as (export pack)", null),
            ],
            RecipeOp.Convert => [("format", "Format", ["jpg", "png", "webp", "avif", "tiff"]), ("quality", "Quality", null)],
            RecipeOp.Compress => [("maxBytes", "Max bytes", null), ("format", "Format", ["jpg", "webp", "avif"]), ("allowDownscale", "May reduce size", ["true", "false"])],
            RecipeOp.RemoveMetadata => [("remove", "Remove", ["all", "location,camera,thumbnail", "location", "camera", "dates", "author"])],
            RecipeOp.CombineToPdf => [("page", "Page size", ["a4", "letter", "fit"])],
            _ => [],
        };
    }

    private static Dictionary<string, string> DefaultSettings(RecipeOp op) => op switch
    {
        RecipeOp.RenderPdfPages => new() { ["pages"] = "all", ["dpi"] = "150" },
        RecipeOp.CenterCrop => new() { ["ratio"] = "1:1" },
        RecipeOp.Resize => new() { ["maxEdge"] = "1600" },
        RecipeOp.Frame => new() { ["fill"] = "preset:paper", ["padding"] = "100", ["corner"] = "24", ["shadow"] = "32", ["ratio"] = "auto", ["ratios"] = "" },
        RecipeOp.Convert => new() { ["format"] = "jpg", ["quality"] = "85" },
        RecipeOp.Compress => new() { ["maxBytes"] = (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture), ["format"] = "jpg", ["allowDownscale"] = "true" },
        RecipeOp.RemoveMetadata => new() { ["remove"] = "all" },
        RecipeOp.CombineToPdf => new() { ["page"] = "a4" },
        _ => [],
    };

    // ---- brand styles ---------------------------------------------------------------------------

    private UIElement BrandListButtons()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Button("New style", () =>
        {
            var brand = new BrandPreset { Name = "New style" };
            _host.Brands.Save(brand);
            FillBrands(brand.Id);
        }));
        return panel;
    }

    private void FillBrands(string? select = null)
    {
        _brandList.Items.Clear();
        foreach (var brand in _host.Brands.All())
        {
            var item = new ListBoxItem { Content = new TextBlock { Text = brand.Name, FontWeight = FontWeights.SemiBold }, Tag = brand };
            _brandList.Items.Add(item);
            if (brand.Id == (select ?? _brand?.Id)) _brandList.SelectedItem = item;
        }
        if (_brandList.Items.Count == 0)
            _brandEditor.Content = Caption("No brand styles yet. A style keeps your background, spacing, logo and caption together so every export looks the same. Use \"New style\".");
        else if (_brandList.SelectedItem is null) _brandList.SelectedIndex = 0;
    }

    private void ShowBrand(BrandPreset brand)
    {
        _brand = brand;
        var panel = new StackPanel();
        var preview = new Image { Height = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        var name = new TextBox { Text = brand.Name };
        var fill = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var f in BackgroundPresets.Gradients.Select(g => "preset:" + g.Id).Concat(BackgroundPresets.Solids)) fill.Items.Add(f);
        if (!fill.Items.Contains(brand.Fill)) fill.Items.Add(brand.Fill);
        fill.SelectedItem = brand.Fill;
        var padding = new TextBox { Text = brand.Padding.ToString(CultureInfo.InvariantCulture), Width = 80 };
        var corner = new TextBox { Text = brand.Corner.ToString(CultureInfo.InvariantCulture), Width = 80 };
        var shadow = new TextBox { Text = brand.Shadow.ToString(CultureInfo.InvariantCulture), Width = 80 };
        var logo = new TextBox { Text = brand.LogoPath ?? "", IsReadOnly = true };
        var logoPos = new ComboBox { Items = { "bottom-right", "bottom-left", "top-right", "top-left", "bottom-center" }, SelectedItem = brand.LogoPosition, MinWidth = 140 };
        var logoScale = new TextBox { Text = (brand.LogoScale * 100).ToString("0", CultureInfo.InvariantCulture), Width = 80 };
        var caption = new TextBox { Text = brand.Caption ?? "" };
        var captionPos = new ComboBox { Items = { "bottom", "top" }, SelectedItem = brand.CaptionPosition, MinWidth = 100 };
        var captionColor = new TextBox { Text = brand.CaptionColor, Width = 100 };
        var font = new TextBox { Text = brand.FontFamily, Width = 160 };

        BrandPreset Current()
        {
            int I(TextBox t, int fallback) => int.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            double D(TextBox t, double fallback) => double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            return brand with
            {
                Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : brand.Name,
                Fill = fill.SelectedItem as string ?? brand.Fill,
                Padding = I(padding, brand.Padding),
                Corner = I(corner, brand.Corner),
                Shadow = I(shadow, brand.Shadow),
                LogoPath = string.IsNullOrWhiteSpace(logo.Text) ? null : logo.Text,
                LogoPosition = logoPos.SelectedItem as string ?? brand.LogoPosition,
                LogoScale = Math.Clamp(D(logoScale, brand.LogoScale * 100) / 100, 0.02, 0.6),
                Caption = string.IsNullOrWhiteSpace(caption.Text) ? null : caption.Text,
                CaptionPosition = captionPos.SelectedItem as string ?? brand.CaptionPosition,
                CaptionColor = captionColor.Text.Trim(),
                FontFamily = font.Text.Trim().Length > 0 ? font.Text.Trim() : "Segoe UI",
            };
        }
        async void Refresh() => preview.Source = await RenderBrandPreviewAsync(Current());

        panel.Children.Add(preview);
        panel.Children.Add(Field("Name", name));
        panel.Children.Add(Field("Background", fill));
        var spacing = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (l, box) in new[] { ("Padding", padding), ("Corners", corner), ("Shadow", shadow) })
        {
            var pair = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            pair.Children.Add(new TextBlock { Text = l + " (px)", Style = StudioWindow.Res("Text.Label") });
            pair.Children.Add(box);
            spacing.Children.Add(pair);
        }
        panel.Children.Add(spacing);
        var logoRow = new DockPanel();
        var browse = Button("Logo…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Logos|*.png;*.svg;*.webp;*.jpg|All files|*.*" };
            if (dialog.ShowDialog(this) == true) { logo.Text = dialog.FileName; Refresh(); }
        });
        var none = Button("None", () => { logo.Text = ""; Refresh(); });
        var logoButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        logoButtons.Children.Add(browse);
        logoButtons.Children.Add(none);
        DockPanel.SetDock(logoButtons, Dock.Right);
        logoRow.Children.Add(logoButtons);
        logoRow.Children.Add(logo);
        panel.Children.Add(Field("Logo / watermark", logoRow, "PNG with transparency or SVG works best."));
        panel.Children.Add(Field("Logo position", logoPos));
        panel.Children.Add(Field("Logo width (% of canvas)", logoScale));
        panel.Children.Add(Field("Caption", caption, "Arabic and English text are both supported."));
        panel.Children.Add(Field("Caption position", captionPos));
        panel.Children.Add(Field("Caption colour", captionColor));
        panel.Children.Add(Field("Font", font));

        foreach (var box in new[] { padding, corner, shadow, logoScale, caption, captionColor, font }) box.LostKeyboardFocus += (_, _) => Refresh();
        foreach (var combo in new[] { fill, logoPos, captionPos }) combo.SelectionChanged += (_, _) => Refresh();

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var save = Button("Save", () => { var updated = Current(); _host.Brands.Save(updated); _brand = updated; FillBrands(updated.Id); });
        save.Style = StudioWindow.Res("Toolbox.Button.Accent");
        actions.Children.Add(save);
        actions.Children.Add(Button("Delete", () => { _host.Brands.Delete(brand.Id); _brand = null; FillBrands(); }));
        panel.Children.Add(actions);
        panel.Children.Add(Caption("Use a style in the editor (Background → Brand style) or in a recipe step \"Frame / brand style\"."));
        _brandEditor.Content = panel;
        Refresh();
    }

    /// <summary>Renders the style on a built-in sample picture with the real renderer.</summary>
    private static async Task<BitmapSource?> RenderBrandPreviewAsync(BrandPreset brand)
    {
        try
        {
            return await Task.Run(() =>
            {
                var sample = SamplePicture();
                using var source = StudioSource.Open(sample);
                var doc = new EditDocument
                {
                    SourcePath = sample,
                    Background = new BackgroundSettings { Enabled = true, Fill = brand.Fill, Padding = brand.Padding, Corner = brand.Corner, Shadow = brand.Shadow, Ratio = "4:5" },
                    Brand = BrandingSettings.From(brand),
                };
                var frame = StudioRenderer.RenderPreview(doc, source, RenderStage.Final, 420);
                var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null, frame.Bgra, frame.Width * 4);
                bitmap.Freeze();
                return bitmap;
            });
        }
        catch
        {
            return null;
        }
    }

    private static string SamplePicture()
    {
        var path = IOPath.Combine(AppPaths.DataDir, "brand-sample.png");
        if (File.Exists(path)) return path;
        using var x = NetVips.Image.Xyz(960, 600);
        using var r = x[0] * (180.0 / 960) + 40;
        using var g = x[1] * (140.0 / 600) + 80;
        using var b = (r + g) / 2 + 30;
        using var rgb = r.Bandjoin(g, b).Cast(NetVips.Enums.BandFormat.Uchar).Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        rgb.Pngsave(path);
        return path;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static FrameworkElement Field(string label, FrameworkElement control, string? help = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(new TextBlock { Text = label, Style = StudioWindow.Res("Text.Label"), Margin = new Thickness(0, 0, 0, 3) });
        if (!double.IsNaN(control.Width) || control is ComboBox) control.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        if (help is not null) stack.Children.Add(new TextBlock { Text = help, Style = StudioWindow.Res("Text.Caption"), Margin = new Thickness(0, 2, 0, 0) });
        return stack;
    }

    private static TextBlock Heading(string text) => new() { Text = text, Style = StudioWindow.Res("Text.Title"), FontSize = 13.5, Margin = new Thickness(0, 14, 0, 6) };

    private static TextBlock Caption(string text) => new() { Text = text, Style = StudioWindow.Res("Text.Caption"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Small(string text, bool enabled, Action click)
    {
        var b = new Button { Content = text, IsEnabled = enabled, Width = 28, Height = 26, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static string OutputNamerSafe(string name) => Toolbox.Core.Output.OutputNamer.Sanitize(name);
}
