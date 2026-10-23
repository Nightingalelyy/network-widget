using System;
using System.Globalization;
using System.IO;
using System.Xml;

namespace NetSpeedWidget
{
    internal sealed class Settings
    {
        private readonly string path;
        public int? X;
        public int? Y;
        public bool AlwaysOnTop = true;
        public bool UseBits;
        public bool ShowInTaskbar;
        public bool Expanded;
        public string AdapterId = "";

        public Settings(string filePath) { path = filePath; }

        public static Settings Load(string path)
        {
            Settings settings = new Settings(path);
            try
            {
                XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, options))
                {
                    XmlDocument doc = new XmlDocument { XmlResolver = null };
                    doc.Load(reader);
                    XmlElement root = doc.DocumentElement;
                    int x, y;
                    bool flag;
                    if (int.TryParse(root.GetAttribute("x"), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) settings.X = x;
                    if (int.TryParse(root.GetAttribute("y"), NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) settings.Y = y;
                    if (bool.TryParse(root.GetAttribute("topmost"), out flag)) settings.AlwaysOnTop = flag;
                    if (bool.TryParse(root.GetAttribute("bits"), out flag)) settings.UseBits = flag;
                    if (bool.TryParse(root.GetAttribute("taskbar"), out flag)) settings.ShowInTaskbar = flag;
                    if (bool.TryParse(root.GetAttribute("expanded"), out flag)) settings.Expanded = flag;
                    settings.AdapterId = root.GetAttribute("adapter");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
            return settings;
        }

        public bool Save()
        {
            try
            {
                using (XmlWriter writer = XmlWriter.Create(path + ".tmp", new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("NetSpeedWidget");
                    if (X.HasValue) writer.WriteAttributeString("x", X.Value.ToString(CultureInfo.InvariantCulture));
                    if (Y.HasValue) writer.WriteAttributeString("y", Y.Value.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("topmost", AlwaysOnTop.ToString());
                    writer.WriteAttributeString("bits", UseBits.ToString());
                    writer.WriteAttributeString("taskbar", ShowInTaskbar.ToString());
                    writer.WriteAttributeString("expanded", Expanded.ToString());
                    writer.WriteAttributeString("adapter", AdapterId ?? "");
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
