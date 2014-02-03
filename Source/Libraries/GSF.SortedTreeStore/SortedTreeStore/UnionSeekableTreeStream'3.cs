﻿//******************************************************************************************************
//  UnionSeekableTreeStream'3.cs - Gbtc
//
//  Copyright © 2013, Grid Protection Alliance.  All Rights Reserved.
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
//  10/26/2013 - Steven E. Chisholm
//       Generated original version of source code. 
//       
//
//******************************************************************************************************

using System;
using System.Collections.Generic;
using GSF.SortedTreeStore.Tree;

namespace GSF.SortedTreeStore
{
    /// <summary>
    /// Creates a <see cref="SeekableTreeStream{TKey,TValue}"/> that is the union of a collection of 
    /// other SeekableKeyValueStream.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public class UnionSeekableTreeStream<T, TKey, TValue>
        : SeekableTreeStream<TKey, TValue>
        where T : SeekableTreeStream<TKey, TValue>
        where TKey : class, ISortedTreeKey<TKey>, new()
        where TValue : class, ISortedTreeValue<TValue>, new()
    {
        CustomSortHelper<T> m_tables;
        T m_firstTable;
        SortedTreeKeyMethodsBase<TKey> m_keyMethods;
        SortedTreeValueMethodsBase<TValue> m_valueMethods;

        public UnionSeekableTreeStream(IEnumerable<T> list)
        {
            m_keyMethods = new TKey().CreateKeyMethods();
            m_valueMethods = new TValue().CreateValueMethods();
            m_tables = new CustomSortHelper<T>(list, CompareStreams);
        }

        protected int CompareStreams(T item1, T item2)
        {
            if (!item1.IsValid && !item2.IsValid)
                return 0;
            if (!item1.IsValid)
                return 1;
            if (!item2.IsValid)
                return -1;
            return m_keyMethods.CompareTo(item1.CurrentKey, item2.CurrentKey);// item1.CurrentKey.CompareTo(item2.CurrentKey);
        }

        public override bool Read()
        {
            if (m_firstTable == null || !m_firstTable.IsValid)
            {
                IsValid = false;
                return false;
            }
            else
            {
                m_keyMethods.Copy(m_firstTable.CurrentKey, CurrentKey);
                m_valueMethods.Copy(m_firstTable.CurrentValue, CurrentValue);
                m_firstTable.Read();

                if (m_tables.Items.Length >= 2)
                {
                    //If list is no longer in order
                    int compare = CompareStreams(m_firstTable, m_tables[1]);
                    if (compare == 0 && m_firstTable.IsValid)
                    {
                        //If a duplicate entry is found, advance the position of the duplicate entry
                        RemoveDuplicatesFromList();
                    }
                    if (compare > 0)
                    {
                        m_tables.SortAssumingIncreased(0);
                        m_firstTable = m_tables[0];
                    }
                }
                IsValid = true;
                return true;
            }

        }

        public override void SeekToKey(TKey key)
        {
            foreach (var table in m_tables.Items)
            {
                table.SeekToKey(key);
                table.Read();
            }
            m_tables.Sort();


            //Remove any duplicates
            if (m_tables.Items.Length >= 2)
            {
                if (CompareStreams(m_tables[0], m_tables[1]) == 0 && m_tables[0].IsValid)
                {
                    //If a duplicate entry is found, advance the position of the duplicate entry
                    RemoveDuplicatesFromList();
                }
            }

            if (m_tables.Items.Length > 0)
                m_firstTable = m_tables[0];

            IsValid = false;
        }

        /// <summary>
        /// Seeks the streams only in the forward direction.
        /// This means that if the current position in any stream is invalid or past this point,
        /// the stream will not seek backwards.
        /// After returning, the <see cref="TreeStream{TKey,TValue}.CurrentKey"/> 
        /// and <see cref="TreeStream{TKey,TValue}.CurrentValue"/> will only be valid
        /// if it's position is greater then or equal to <see cref="key"/>.
        /// Bug Consideration: When seeking forward, Don't forget to check <see cref="TreeStream{TKey,TValue}.IsValid"/> to see if the first
        /// sample point in this list is still valid. If not, you will accidentially skip the first sample point.
        /// </summary>
        /// <param name="key"></param>
        public void SeekForward(TKey key)
        {
            foreach (var table in m_tables.Items)
            {
                if (table.IsValid && m_keyMethods.IsLessThan(table.CurrentKey, key)) // table.CurrentKey.IsLessThan(key))
                {
                    table.SeekToKey(key);
                    table.Read();
                }
                if (table.IsValid && m_keyMethods.IsLessThan(table.CurrentKey, key)) // table.CurrentKey.IsLessThan(key))
                {
                    table.SeekToKey(key);
                    table.Read();
                    throw new Exception("should never occur");
                }
            }
            m_tables.Sort();

            //Remove any duplicates
            if (m_tables.Items.Length >= 2)
            {
                if (CompareStreams(m_tables[0], m_tables[1]) == 0 && m_tables[0].IsValid)
                {
                    //If a duplicate entry is found, advance the position of the duplicate entry
                    RemoveDuplicatesFromList();
                }
            }

            if (m_tables.Items.Length > 0)
                m_firstTable = m_tables[0];

            if (m_keyMethods.IsLessThan(CurrentKey, key))//CurrentKey.IsLessThan(key))
            {
                IsValid = false;
            }
        }

        void RemoveDuplicatesFromList()
        {
            for (int index = 1; index < m_tables.Items.Length; index++)
            {
                if (CompareStreams(m_tables[0], m_tables[index]) == 0)
                {
                    m_tables[index].Read();
                }
                else
                {
                    for (int j = index; j > 0; j--)
                        m_tables.SortAssumingIncreased(j);
                    break;
                }
            }
            //m_tables.Sort();
        }

    }
}
