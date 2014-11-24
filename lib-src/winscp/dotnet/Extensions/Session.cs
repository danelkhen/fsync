using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinSCP
{
    public class SynchronizeOptions
    {
        public bool Preview { get; set; }
    }

    partial class Session
    {
        public SynchronizationResult SynchronizeDirectories2(
            SynchronizationMode mode, string localPath, string remotePath,
            bool removeFiles, bool mirror = false, SynchronizationCriteria criteria = SynchronizationCriteria.Time,
            TransferOptions options = null, SynchronizeOptions options2 = null)
        {
            using (Logger.CreateCallstackAndLock())
            {
                if (options == null)
                {
                    options = new TransferOptions();
                }

                CheckOpened();

                if (removeFiles && (mode == SynchronizationMode.Both))
                {
                    throw new ArgumentException("Cannot delete files in synchronization mode Both");
                }

                if (mirror && (mode == SynchronizationMode.Both))
                {
                    throw new ArgumentException("Cannot mirror files in synchronization mode Both");
                }

                if ((criteria != SynchronizationCriteria.Time) && (mode == SynchronizationMode.Both))
                {
                    throw new ArgumentException("Only Time criteria is allowed in synchronization mode Both");
                }

                string modeName;
                switch (mode)
                {
                    case SynchronizationMode.Local:
                        modeName = "local";
                        break;
                    case SynchronizationMode.Remote:
                        modeName = "remote";
                        break;
                    case SynchronizationMode.Both:
                        modeName = "both";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("mode");
                }

                string criteriaName;
                switch (criteria)
                {
                    case SynchronizationCriteria.None:
                        criteriaName = "none";
                        break;
                    case SynchronizationCriteria.Time:
                        criteriaName = "time";
                        break;
                    case SynchronizationCriteria.Size:
                        criteriaName = "size";
                        break;
                    case SynchronizationCriteria.Either:
                        criteriaName = "either";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("criteria");
                }

                WriteCommand(
                    string.Format(CultureInfo.InvariantCulture,
                        "synchronize {0} {1} {2} {3} {7} -criteria=\"{4}\" -- \"{5}\" \"{6}\"",
                        modeName,
                        BooleanSwitch(removeFiles, "delete"),
                        BooleanSwitch(mirror, "mirror"),
                        options.ToSwitches(),
                        criteriaName,
                        ArgumentEscape(localPath), ArgumentEscape(remotePath),
                        BooleanSwitch(options2.Preview, "preview"))
                        );

                return ReadSynchronizeDirectories();
            }
        }

    }
}
