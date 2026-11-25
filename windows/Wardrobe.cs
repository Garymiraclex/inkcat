using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace InkCat
{
    /// <summary>当前的装扮：每个部位选了哪个素材包。改选择、改位置、改素材文件都会重新合成，猫和设置窗口跟着更新。</summary>
    sealed class Wardrobe
    {
        public static readonly Wardrobe Shared = new Wardrobe();

        public Pack[] Packs { get; private set; } = Library.Scan();
        public Dictionary<Slot, string> Problems { get; private set; } = new Dictionary<Slot, string>();
        public Dictionary<Slot, string> Warnings { get; private set; } = new Dictionary<Slot, string>();
        public bool Busy { get; private set; }
        public Look Look { get; private set; } = new Look();

        /// <summary>换好装后通知；第一次也会通知。</summary>
        public event Action<Look> Dressed;
        /// <summary>选择、位置、提示有变化。</summary>
        public event Action Changed;

        readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly DispatcherTimer settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        FileSystemWatcher watcher;
        Dictionary<Slot, Choice> job;
        Pack fallback;
        int turn;
        string worn;

        Wardrobe()
        {
            if (Prefs.Get("skin", "") == "gamer")  // 早先试用版的「电竞」
                foreach (var slot in new[] { Slot.Keyboard, Slot.Pad, Slot.Mouse, Slot.Headset }) Prefs.Set(Key(slot), "gamer");
            Prefs.Set("skin", "");
        }

        public void Start()
        {
            Library.Prepare();
            settle.Tick += (s, e) => { settle.Stop(); Rescan(); };
            void Touched(object s, EventArgs e) => ui.BeginInvoke(new Action(() => { settle.Stop(); settle.Start(); }));  // 改动停下来后再看
            try
            {
                watcher = new FileSystemWatcher(Library.Folder) { IncludeSubdirectories = true, InternalBufferSize = 64 << 10 };
                watcher.Changed += Touched;
                watcher.Created += Touched;
                watcher.Deleted += Touched;
                watcher.Renamed += Touched;
                watcher.Error += Touched;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is IOException || e is ArgumentException) { }
            new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            Rescan();
        }

        void Rescan()
        {
            try { Packs = Library.Scan(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { settle.Start(); return; }
            Refresh();
        }

        // ———— 选择 ————

        static string Key(Slot slot) => "wear." + slot.Id();

        /// <summary>选中的素材包；配件没选时为空串。</summary>
        public string Choice(Slot slot) => Prefs.Get(Key(slot), slot.IsOptional() ? "" : Pack.Classic);

        public void Choose(Slot slot, string id)
        {
            Prefs.Set(Key(slot), id);
            Refresh();
        }

        public IEnumerable<Pack> PacksFor(Slot slot) => Packs.Where(p => p.Slots.Contains(slot));
        public Pack Worn(Slot slot) => PacksFor(slot).FirstOrDefault(p => p.Id == Choice(slot));

        /// <summary>一次换成同一个素材包里有的所有部位，其余恢复原版。</summary>
        public void WearAll(Pack pack)
        {
            foreach (var slot in Slots.All) Prefs.Set(Key(slot), pack.Slots.Contains(slot) ? pack.Id : slot.IsOptional() ? "" : Pack.Classic);
            Refresh();
        }

        // ———— 位置：自己的素材写进 skin.json，跟着素材包走；内置的记在设置里 ————

        public Place PlaceOf(Slot slot)
        {
            var pack = slot == Slot.Cat ? null : Worn(slot);
            if (pack == null) return new Place();
            try
            {
                if (pack.IsBuiltin) return (new JavaScriptSerializer().Deserialize<Place>(Prefs.Get($"place.{slot.Id()}.{pack.Id}", "{}")) ?? new Place()).Clamped();
            }
            catch (Exception) { return new Place(); }
            return pack.Config.place != null && pack.Config.place.TryGetValue(slot.Id(), out var p) && p != null ? p.Clamped() : new Place();
        }

        public void Move(Slot slot, Place place)
        {
            var pack = Worn(slot);
            if (pack == null || slot == Slot.Cat) return;
            if (pack.IsBuiltin) Prefs.Set($"place.{slot.Id()}.{pack.Id}", new JavaScriptSerializer().Serialize(place));
            else
            {
                pack.Config.place = pack.Config.place ?? new Dictionary<string, Place>();
                pack.Config.place[slot.Id()] = place;
                Library.SavePlace(pack, slot, place);
            }
            Refresh();
        }

        // ———— 合成 ————

        void Refresh()
        {
            var original = Packs.First(p => p.Id == Pack.Classic);
            var outfit = new Dictionary<Slot, Choice>();
            var missing = new Dictionary<Slot, string>();
            foreach (var slot in Slots.All)
            {
                var pack = Worn(slot);
                if (pack != null) outfit[slot] = new Choice { Pack = pack, Place = PlaceOf(slot) };
                else
                {
                    if (Choice(slot) != "") missing[slot] = MissingText(slot);
                    if (!slot.IsOptional()) outfit[slot] = new Choice { Pack = original };
                }
            }
            Missing = missing;
            lock (wake) (job, fallback, turn) = (outfit, original, turn + 1);
            Busy = true;
            Changed?.Invoke();
            wake.Set();
        }

        Dictionary<Slot, string> Missing = new Dictionary<Slot, string>();

        string MissingText(Slot slot)
        {
            var pack = Packs.FirstOrDefault(p => p.Id == Choice(slot));
            return pack != null ? $"素材包「{pack.Title}」里没有完整的{slot.Title()}" : $"找不到素材包「{Choice(slot).Replace("user/", "")}」";
        }

        /// <summary>后台线程：连续改动时只算最后一次。</summary>
        void Work()
        {
            var composer = new Composer();
            while (true)
            {
                wake.WaitOne();
                Dictionary<Slot, Choice> outfit;
                Pack original;
                int mine;
                lock (wake) (outfit, original, mine) = (job, fallback, turn);
                Composer.Result result;
                try { result = composer.Compose(outfit, original); }
                catch (Problem) { continue; }  // 算到一半文件又变了，马上会再通知
                ui.BeginInvoke(new Action(() => Finish(result, mine)));
            }
        }

        void Finish(Composer.Result result, int mine)
        {
            if (mine != turn) return;
            Busy = false;
            Problems = Missing.Concat(result.Problems.Where(p => !Missing.ContainsKey(p.Key))).ToDictionary(p => p.Key, p => p.Value);
            Warnings = result.Warnings;
            string ids = string.Join("|", Slots.All.Select(Choice));
            if (result.Problems.Count == 0 || worn != ids)  // 图改坏了，先穿着上一次的
            {
                if (result.Problems.Count == 0) worn = ids;
                Look = result.Look;
                Keyboard.Rows = Look.Rows;
                Dressed?.Invoke(Look);
            }
            Changed?.Invoke();
        }
    }
}
