using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Data
{
    public class AudioConfiguration
    {
        public MediaFile CreationFile { get; set; }

        private List<AudioConfigurationEntry> _config;

        public AudioConfiguration (MediaFile file, List<AudioConfigurationEntry> cfg)
        {
            _config = cfg;
            CreationFile = file;
        }

        public List<AudioConfigurationEntry> GetConfig (MediaFile file)
        {
            // Nothing to match the configuration against. Callers that build audio arguments without a
            // file in hand - the AV1AN tab does, having no per-track settings of its own - have no use
            // for one either, and dereferencing the missing file threw before reaching that conclusion.
            if (file == null || CreationFile == null)
                return null;

            if (file.ImportPath != CreationFile.ImportPath || file.Size != CreationFile.Size)
                return null; // Return null if the file has changed
            else
                return _config;
        }
    }

    public class AudioConfigurationEntry
    {
        public int AudioIndex { get; set; }
        public int ChannelCount { get; set; }
        public int BitrateKbps { get; set; }

        public AudioConfigurationEntry (int audIndex, int channels, int kbps)
        {
            AudioIndex = audIndex;
            ChannelCount = channels;
            BitrateKbps = kbps;
        }
    }
}
