using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core.Pulse.Chat;
using Toolbox.Windows.Pulse.Chat;

namespace Toolbox.Windows.Tests;

public partial class HomeWindowTests
{
    private sealed class StillRunner : IAgentRunner
    {
        public List<AgentLaunch> Started { get; } = [];

        public IAgentTurn Start(AgentLaunch launch, Action<AgentEvent> onEvent)
        {
            Started.Add(launch);
            return new StillTurn();
        }

        private sealed class StillTurn : IAgentTurn
        {
            private readonly TaskCompletionSource _done = new();
            public Task Completion => _done.Task;
            public void Stop() => _done.TrySetResult();
        }
    }

    /// <summary>
    /// Pulse's chat window on the owner's example (a clothing gallery built by Claude, pictured by
    /// Codex): a group thread with a hand-off, pictures to approve, a card to allow a command and the
    /// turn-limit card, plus a one-to-one chat and a quick chat in the list; and the New chat window.
    /// Nothing runs: the agents are stand-ins that never answer.
    /// </summary>
    private static void RenderChats(string? snapshots, string theme, string lang)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amirlap-chatwin-" + Guid.NewGuid().ToString("N"));
        var folder = System.IO.Path.Combine(dir, "brand-gallery");
        var pictures = System.IO.Path.Combine(folder, "public", "garments");
        System.IO.Directory.CreateDirectory(pictures);
        var runner = new StillRunner();
        var hub = new ChatHub(new ChatStore(System.IO.Path.Combine(dir, "chats")), runner, a => a + ".exe", a => a()) { CodexPictures = null };
        try
        {
            var shirts = new[] { ("linen-shirt", Color.FromRgb(0xE9, 0xDD, 0xC6)), ("black-hoodie", Color.FromRgb(0x2A, 0x2A, 0x30)), ("wide-trousers", Color.FromRgb(0x8C, 0x9B, 0x7C)), ("denim-jacket", Color.FromRgb(0x5C, 0x7E, 0xB2)) }
                .Select(s => Garment(pictures, s.Item1, s.Item2)).ToList();
            var thumb = ChatViews.LoadThumbnail(shirts[0]);
            Assert.NotNull(thumb);
            Assert.True(thumb!.Height > 0);

            var quick = hub.Create(new ChatInfo { Title = "Name ideas for the brand", Agents = [Agent.Codex], Permission = ChatPermission.Read });
            quick.Messages.Add(new ChatMessage { Author = ChatAuthor.User, Text = "Give me six short names for a modest clothing brand.", At = DateTimeOffset.UtcNow.AddHours(-3) });
            quick.Messages.Add(new ChatMessage { Author = Agent.Codex, Text = "Here are six to start: **Sada**, Lumen, Mada, Wisp, Kaffa and Noor Thread.", At = DateTimeOffset.UtcNow.AddHours(-3) });
            var one = hub.Create(new ChatInfo { Title = "Product data", Folder = folder, Agents = [Agent.Claude], Permission = ChatPermission.Ask });
            one.Messages.Add(new ChatMessage { Author = ChatAuthor.User, Text = "Add sizes XS to XL and a price to every garment in `garments.json`.", At = DateTimeOffset.UtcNow.AddHours(-2) });
            one.Messages.Add(new ChatMessage { Author = Agent.Claude, Text = "Done. All 9 garments now have sizes XS–XL and an empty price for you to fill in.", At = DateTimeOffset.UtcNow.AddHours(-2), Steps = [new() { Kind = "read", Text = "Read garments.json" }, new() { Kind = "edit", Text = "Edited garments.json" }] });
            one.Info.Unread = true;

            var info = new ChatInfo
            {
                Title = "Gallery build",
                Folder = folder,
                Agents = [Agent.Claude, Agent.Codex],
                Recipe = ChatRecipe.Builder,
                Lead = Agent.Claude,
                Permission = ChatPermission.Edit,
                HopLimit = 4,
                Owns = new() { [Agent.Claude] = ["src/", "index.html"], [Agent.Codex] = ["public/garments/"] },
                Sessions = new() { [Agent.Claude] = "6bc4efd5-906d-4bfb-9e78-7c5e84305838", [Agent.Codex] = "01a1dba0-b3e3-7ff1-b94c-56bd8c3fbad4" },
                Hops = 4,
            };
            var gallery = hub.Create(info);
            var m = gallery.Messages;
            var at = DateTimeOffset.UtcNow.AddMinutes(-12);
            m.Add(new ChatMessage { Author = ChatAuthor.User, At = at, Text = "@claude build a 2D gallery for the brand: a grid of garments, and a page for each one with front, back and side views. @codex make those three views for every garment in `garments.json`." });
            m.Add(new ChatMessage
            {
                Author = Agent.Claude, At = at.AddMinutes(1),
                Text = "Plan:\n- `garments.json` with the 9 garments (name, colour, fabric)\n- the gallery grid and a page per garment in `src/`\n\nEach garment's pictures go in `public/garments/<id>/` as front, back and side.",
                Steps = [new() { Kind = "read", Text = "Read brand-notes.md" }, new() { Kind = "edit", Text = "Wrote garments.json" }, new() { Kind = "edit", Text = "Wrote src/Gallery.tsx" }, new() { Kind = "run", Text = "Ran npm run build" }],
            });
            m.Add(new ChatMessage { Author = Agent.Claude, At = at.AddMinutes(3), To = Agent.Codex, Text = "@codex `garments.json` is ready with 9 garments. Start with the front views, so the owner can approve the style before you make backs and sides." });
            m.Add(new ChatMessage { Author = Agent.Codex, At = at.AddMinutes(6), Text = "Fronts for the first four are ready. Pictures use limits 3–5× faster than text, so I'll wait for your OK on the style.", Images = shirts, Steps = [new() { Kind = "image", Text = "Made a picture" }, new() { Kind = "edit", Text = "Created public/garments/linen-shirt/front.png" }] });
            hub.Approve(gallery.Info.Id, shirts[0], Agent.Codex, true);
            m.Add(new ChatMessage { Author = ChatAuthor.Pulse, Kind = ChatKind.Denied, About = Agent.Claude, State = CardState.Open, At = at.AddMinutes(7), Denied = [new() { Tool = "Bash", Summary = "Run npm install framer-motion", Rule = "Bash(npm install framer-motion)", AlwaysRule = "Bash(npm install:*)" }] });
            m.Add(new ChatMessage { Author = ChatAuthor.Pulse, Kind = ChatKind.Limit, About = Agent.Codex, State = CardState.Open, At = at.AddMinutes(8), Text = "4" });
            gallery.Info.Updated = DateTimeOffset.UtcNow;

            var window = new ChatWindow(hub, a => a == Agent.Codex ? @"C:\Users\User\.codex\.sandbox-bin\codex.exe" : @"C:\Users\User\AppData\Roaming\npm\claude.exe");
            window.Select(gallery.Info.Id);
            SnapshotChat(snapshots, window, 1160, 780, $"pulse-chat-{theme}-{lang}");
            if (lang == "en") SnapshotChat(snapshots, window, 1160, 1700, $"pulse-chat-tall-{theme}");
            Assert.Equal(gallery.Info.Id, window.Current);
            Assert.Equal(1, hub.NeedsYou);

            // Allowing the command sends Claude off again (to the stand-in that never answers).
            var card = m.First(x => x.Kind == ChatKind.Denied);
            hub.Answer(gallery.Info.Id, card.Id, CardState.Allowed);
            Assert.Contains("Bash(npm install framer-motion)", runner.Started.Last().Args);
            Assert.Equal(Agent.Claude, hub.WorkingIn(gallery.Info.Id));
            var redrawn = DateTime.UtcNow.AddMilliseconds(400);
            Pump(() => DateTime.UtcNow > redrawn, "the redraw", 5);
            SnapshotChat(snapshots, window, 1160, 780, $"pulse-chat-working-{theme}-{lang}");
            hub.Stop(gallery.Info.Id);
            window.Select(one.Info.Id);
            SnapshotChat(snapshots, window, 1160, 780, $"pulse-chat-one-{theme}-{lang}");
            window.Close();

            var empty = new ChatHub(new ChatStore(System.IO.Path.Combine(dir, "none")), runner, _ => null, a => a());
            var blank = new ChatWindow(empty, _ => null);
            SnapshotChat(snapshots, blank, 1160, 780, $"pulse-chat-empty-{theme}-{lang}");
            blank.Close();

            var dialog = new NewChatWindow(hub, a => a + ".exe", folder);
            SnapshotChat(snapshots, dialog, 880, 700, $"pulse-newchat-{theme}-{lang}");
            dialog.Close();
        }
        finally
        {
            hub.Dispose();
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    private static void SnapshotChat(string? snapshots, Window window, double width, double height, string name)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new Border { Child = content };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(frame, width, height);
        Save(snapshots, frame, width, height, name);
        frame.Child = null;
        window.Content = content;
    }

    /// <summary>A made-up garment picture (a shirt shape in a colour) saved as front.png in its own folder.</summary>
    private static string Garment(string root, string id, Color colour)
    {
        var dir = System.IO.Path.Combine(root, id);
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "front.png");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF1, 0xEC)), null, new Rect(0, 0, 240, 300));
            var shape = Geometry.Parse("M86,40 L40,62 L14,120 L48,136 L62,112 L62,262 L178,262 L178,112 L192,136 L226,120 L200,62 L154,40 C146,58 134,66 120,66 C106,66 94,58 86,40 Z");
            dc.DrawGeometry(new SolidColorBrush(colour), new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 2), shape);
        }
        var bmp = new RenderTargetBitmap(240, 300, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
