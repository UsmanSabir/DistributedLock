﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Medallion.Threading.Sql;
using System.Data.Common;
using System.Data.SqlClient;
using System.Threading;

namespace Medallion.Threading.Tests.Sql
{
    [TestClass]
    public sealed class SqlDistributedReaderWriterLockTest : TestBase
    {
        [TestMethod]
        public void TestBadConstructorArguments()
        {
            TestHelper.AssertThrows<ArgumentNullException>(() => new SqlDistributedReaderWriterLock(null, ConnectionStringProvider.ConnectionString));
            TestHelper.AssertThrows<ArgumentNullException>(() => new SqlDistributedReaderWriterLock("a", default(string)));
            TestHelper.AssertThrows<ArgumentNullException>(() => new SqlDistributedReaderWriterLock("a", default(DbTransaction)));
            TestHelper.AssertThrows<ArgumentNullException>(() => new SqlDistributedReaderWriterLock("a", default(DbConnection)));
            TestHelper.AssertThrows<FormatException>(() => new SqlDistributedReaderWriterLock(new string('a', SqlDistributedReaderWriterLock.MaxLockNameLength + 1), ConnectionStringProvider.ConnectionString));
            TestHelper.AssertDoesNotThrow(() => new SqlDistributedReaderWriterLock(new string('a', SqlDistributedReaderWriterLock.MaxLockNameLength), ConnectionStringProvider.ConnectionString));
        }

        [TestMethod]
        public void TestGetSafeLockNameCompat()
        {
            SqlDistributedReaderWriterLock.MaxLockNameLength.ShouldEqual(SqlDistributedLock.MaxLockNameLength);

            var cases = new[]
            {
                string.Empty,
                "abc",
                "\\",
                new string('a', SqlDistributedLock.MaxLockNameLength),
                new string('\\', SqlDistributedLock.MaxLockNameLength),
                new string('x', SqlDistributedLock.MaxLockNameLength + 1)
            };

            foreach (var lockName in cases)
            {
                // should be compatible with SqlDistributedLock
                SqlDistributedReaderWriterLock.GetSafeLockName(lockName).ShouldEqual(SqlDistributedLock.GetSafeLockName(lockName));
            }
        }

        /// <summary>
        /// Tests the logic where upgrading a connection stops and restarts the keepalive
        /// 
        /// NOTE: This is not an abstract test case because it applies ONLY to the combination of 
        /// <see cref="SqlDistributedReaderWriterLock"/> and <see cref="SqlDistributedLockConnectionStrategy.Azure"/>
        /// </summary>
        [TestMethod]
        public void TestAzureStrategyProtectsFromIdleSessionKillerAfterFailedUpgrade()
        {
            var originalInterval = KeepaliveHelper.Interval;
            try
            {
                KeepaliveHelper.Interval = TimeSpan.FromSeconds(.1);

                var @lock = new SqlDistributedReaderWriterLock(
                    nameof(TestAzureStrategyProtectsFromIdleSessionKillerAfterFailedUpgrade), 
                    ConnectionStringProvider.ConnectionString, 
                    SqlDistributedLockConnectionStrategy.Azure
                );
                using (new IdleSessionKiller(ConnectionStringProvider.ConnectionString, idleTimeout: TimeSpan.FromSeconds(.25)))
                using (@lock.AcquireReadLock())
                {
                    var handle = @lock.AcquireUpgradeableReadLock();
                    handle.TryUpgradeToWriteLock().ShouldEqual(false);
                    handle.TryUpgradeToWriteLockAsync().Result.ShouldEqual(false);
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    TestHelper.AssertDoesNotThrow(() => handle.Dispose());
                }
            }
            finally
            {
                KeepaliveHelper.Interval = originalInterval;
            }
        }

        /// <summary>
        /// Demonstrates that we don't multi-thread the connection despite the <see cref="KeepaliveHelper"/>.
        /// 
        /// This test is similar to <see cref="AzureConnectionStrategyTestCases{TEngineFactory}.ThreadSafetyExercise"/>,
        /// but in this case we additionally test lock upgrading which must pause and restart the <see cref="KeepaliveHelper"/>.
        /// 
        /// NOTE: This is not an abstract test case because it applies ONLY to the combination of 
        /// <see cref="SqlDistributedReaderWriterLock"/> and <see cref="SqlDistributedLockConnectionStrategy.Azure"/>
        /// </summary>
        [TestMethod]
        public void ThreadSafetyExerciseWithLockUpgrade()
        {
            var originalInterval = KeepaliveHelper.Interval;
            try
            {
                KeepaliveHelper.Interval = TimeSpan.FromMilliseconds(1);

                TestHelper.AssertDoesNotThrow(() =>
                {
                    var @lock = new SqlDistributedReaderWriterLock(
                        nameof(ThreadSafetyExerciseWithLockUpgrade),
                        ConnectionStringProvider.ConnectionString,
                        SqlDistributedLockConnectionStrategy.Azure
                    );
                    for (var i = 0; i < 30; ++i)
                    {
                        using (var handle = @lock.AcquireUpgradeableReadLockAsync().Result)
                        {
                            Thread.Sleep(1);
                            handle.UpgradeToWriteLock();
                            Thread.Sleep(1);
                        }
                    }
                });
            }
            finally
            {
                KeepaliveHelper.Interval = originalInterval;
            }
        }
    }
}
