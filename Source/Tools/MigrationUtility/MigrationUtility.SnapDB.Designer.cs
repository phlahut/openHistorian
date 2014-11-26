﻿//******************************************************************************************************
//  MigrationUtility.SnapDBWriter.Designer.cs - Gbtc
//
//  Copyright © 2010, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the Eclipse Public License -v 1.0 (the "License"); you may
//  not use this file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://www.opensource.org/licenses/eclipse-1.0.php
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  11/21/2014 - J. Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GSF;
using GSF.Diagnostics;
using GSF.Historian;
using GSF.Historian.Files;
using GSF.IO;
using GSF.Snap;
using GSF.Snap.Collection;
using GSF.Snap.Services;
using GSF.Snap.Services.Reader;
using GSF.Snap.Storage;
using GSF.Units;
using openHistorian.Net;
using openHistorian.Snap;

namespace MigrationUtility
{
    // SnapDB Engine Code
    partial class MigrationUtility
    {
        private HistorianServer m_server;
        private SnapClient m_client;
        private ClientDatabaseBase<HistorianKey, HistorianValue> m_clientDatabase;
        private LogSubscriber m_logSubscriber;

        private void OpenSnapDBEngine(string instanceName, string destinationFilesLocation, string targetFileSize, string directoryNamingMethod, bool readOnly = false)
        {
            m_logSubscriber = Logger.CreateSubscriber();
            m_logSubscriber.Subscribe(Logger.RootSource);
            m_logSubscriber.Subscribe(Logger.RootType);
            m_logSubscriber.Verbose = VerboseLevel.NonDebug ^ VerboseLevel.PerformanceIssue ^ VerboseLevel.Information;
            m_logSubscriber.Log += m_logSubscriber_Log;

            if (string.IsNullOrEmpty(instanceName))
                instanceName = "PPA";
            else
                instanceName = instanceName.Trim();

            // Establish archive information for this historian instance
            HistorianServerDatabaseConfig archiveInfo = new HistorianServerDatabaseConfig(instanceName, destinationFilesLocation, !readOnly);

            double targetSize;

            if (!double.TryParse(targetFileSize, out targetSize))
                targetSize = 1.5D;

            archiveInfo.TargetFileSize = (long)(targetSize * SI.Giga);

            int methodIndex;

            if (!int.TryParse(directoryNamingMethod, out methodIndex))
                methodIndex = (int)ArchiveDirectoryMethod.YearThenMonth;

            archiveInfo.DirectoryMethod = (ArchiveDirectoryMethod)methodIndex;

            m_server = new HistorianServer(archiveInfo);
            m_client = SnapClient.Connect(m_server.Host);
            m_clientDatabase = m_client.GetDatabase<HistorianKey, HistorianValue>(instanceName);

            ShowUpdateMessage("[SnapDB] Engine initialized");
        }

        private void CloseSnapDBEngine()
        {
            m_client = null;
            m_clientDatabase = null;

            if ((object)m_server != null)
            {
                m_server.Dispose();
                m_server = null;
            }

            if ((object)m_logSubscriber != null)
            {
                m_logSubscriber.Log -= m_logSubscriber_Log;
                m_logSubscriber = null;
            }

            ShowUpdateMessage("[SnapDB] Engine terminated");
        }

        private void WriteSnapDBData(IDataPoint dataPoint)
        {
            // Copy data point to key and value
            CopyDataPointToKeyValue(dataPoint, m_key, m_value);

            // Write key/value pair to SnapDB engine
            m_clientDatabase.Write(m_key, m_value);
        }

        private IDataPoint ReadSnapDBValue(long timestamp, int pointID)
        {
            TreeStream<HistorianKey, HistorianValue> stream = m_clientDatabase.ReadSingleValue((ulong)timestamp, (ulong)pointID);

            if (stream.Read(m_key, m_value))
                return new ArchiveDataPoint(pointID)
                {
                    Value = BitMath.ConvertToSingle(m_value.Value1),
                    Quality = (Quality)m_value.Value3
                };

            return null;
        }

        private static void CopyDataPointToKeyValue(IDataPoint dataPoint, HistorianKey key, HistorianValue value)
        {
            // Write key information
            key.Timestamp = (ulong)dataPoint.Time.ToDateTime().Ticks;
            key.PointID = (ulong)dataPoint.HistorianID;

            // Note that third ulong in key can be used for storing duplicate timestamps
            // such as those duplicates that may come in during leap-seconds. IData will
            // need to expose this indication such that entry number could be updated
            key.EntryNumber = 0;

            // Since current time-series measurements are basically all floats - values fit into
            // first ulong, this will change as value types accepted by framework expands
            value.Value1 = BitMath.ConvertToUInt64(dataPoint.Value);

            // This value will be used when host framework expands to support multiple data types
            value.Value2 = 0;

            // While leaving second ulong available for expanded data type storage, we store
            // quality in third ulong for future consistency  - note that this still leaves
            // 32-bits of space available for future use
            value.Value3 = (ulong)dataPoint.Quality;
        }

        private void FlushSnapDB()
        {
            m_clientDatabase.HardCommit();
        }

        // Expose SnapDB log messages via Adapter status and exception event raisers
        private void m_logSubscriber_Log(LogMessage logMessage)
        {
            if ((object)logMessage.Exception != null)
                ShowUpdateMessage("[SnapDB] Exception during {0}: {1}", logMessage.EventName, logMessage.GetMessage(true));
            else
                ShowUpdateMessage("[SnapDB] {0}: {1}", logMessage.Level, logMessage.GetMessage(true));
        }

        private class GSFHistorianStream
            : TreeStream<HistorianKey, HistorianValue>
        {
            private ArchiveFile m_file;
            private IEnumerator<IDataPoint> m_enumerator;
            private readonly Dictionary<HistorianKey, int> m_encounteredKeys;
            private bool m_disposed;

            public GSFHistorianStream(MigrationUtility parent, string sourceFileName, string instanceName)
            {
                m_file = OpenArchiveFile(sourceFileName, instanceName);

                // Determine maximum point ID if it hasn't been determined yet
                int m_maxPointID = FindMaximumPointID(m_file.MetadataFile);

                // Create new time-sorted data point scanner to read points in this file in sorted order
                TimeSortedArchiveFileScanner scanner = new TimeSortedArchiveFileScanner();

                // Get start and end times from file data and validate
                TimeTag startTime, endTime;

                startTime = m_file.Fat.FileStartTime;

                if (startTime == TimeTag.MaxValue)
                    startTime = TimeTag.MinValue;

                endTime = m_file.Fat.FileEndTime;

                if (endTime == TimeTag.MinValue)
                    endTime = TimeTag.MaxValue;

                scanner.FileAllocationTable = m_file.Fat;
                scanner.HistorianIDs = Enumerable.Range(1, m_maxPointID);
                scanner.StartTime = startTime;
                scanner.EndTime = endTime;
                scanner.ResumeFrom = null;
                scanner.DataReadExceptionHandler = (sender, e) => parent.ShowUpdateMessage("[GSFHistorian] Exception encountered during data read: {0}", e.Argument.Message);

                m_enumerator = scanner.Read().GetEnumerator();
                m_encounteredKeys = new Dictionary<HistorianKey, int>(m_file.Fat.DataPointsArchived);
            }

            public ArchiveFile ArchiveFile
            {
                get
                {
                    return m_file;
                }
            }

            public int Total
            {
                get
                {
                    return m_encounteredKeys.Count;
                }
            }

            public override bool IsAlwaysSequential
            {
                get
                {
                    return true;
                }
            }

            public override bool NeverContainsDuplicates
            {
                get
                {
                    return true;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (!m_disposed)
                {
                    try
                    {
                        if (disposing)
                        {
                            if ((object)m_file != null)
                                m_file.Dispose();
                        }
                    }
                    finally
                    {
                        m_disposed = true;
                        base.Dispose(disposing);
                    }
                }
            }

            protected override bool ReadNext(HistorianKey key, HistorianValue value)
            {
                if (m_enumerator.MoveNext())
                {
                    int count;

                    CopyDataPointToKeyValue(m_enumerator.Current, key, value);

                    if (m_encounteredKeys.TryGetValue(key, out count))
                    {
                        // Duplicate timestamp encountered, increment entry number
                        count++;
                        key.EntryNumber = (ulong)count;
                        m_encounteredKeys[key] = count;
                    }
                    else
                    {
                        m_encounteredKeys.Add(key.Clone(), 0);
                    }

                    return true;
                }

                return false;
            }
        }
    }
}