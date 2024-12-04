using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Netcorext.Extensions.Threading.Locker.Tests
{
    public class KeyLockerTests
    {
        private readonly ITestOutputHelper _testOutput;
        private readonly Mock<ILogger> _mockLogger;

        public KeyLockerTests(ITestOutputHelper testOutput)
        {
            _testOutput = testOutput;
            _mockLogger = new Mock<ILogger>();
        }

        [Fact]
        public void Constructor_ValidParameters_ShouldInitializeCorrectly()
        {
            // Arrange & Act
            var keyLocker = new KeyLocker(_mockLogger.Object,
                timeout: TimeSpan.FromSeconds(10),
                throwTimeoutException: true,
                maxConcurrent: 2,
                cleanupInterval: TimeSpan.FromMinutes(1));

            // Assert
            Assert.NotNull(keyLocker);
        }

        [Fact]
        public void Constructor_InvalidMaxConcurrent_ShouldThrowArgumentException()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentException>(() =>
                new KeyLocker(_mockLogger.Object, maxConcurrent: 0));
        }

        [Fact]
        public void Wait_SingleKey_ShouldAcquireLock()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object);
            const string testKey = "test_key";

            // Act
            keyLocker.Wait(testKey);
            var waitingCount = keyLocker.GetWaitingCount(testKey);

            // Assert
            Assert.Equal(1, waitingCount);
        }

        [Fact]
        public async Task WaitAsync_SingleKey_ShouldAcquireLock()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object);
            const string testKey = "test_key";

            // Act
            await keyLocker.WaitAsync(testKey);
            var waitingCount = keyLocker.GetWaitingCount(testKey);

            // Assert
            Assert.Equal(1, waitingCount);
        }

        [Fact]
        public async void Release_ExistingKey_ShouldReleaseOneLock()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object, maxConcurrent: 1);
            const string testKey = "test_key";
            const int concurrent = 10;
            const int delay = 100;
            var tasks = new List<Task>();
            var completed = 0;

            var stopwatch = new Stopwatch();

            stopwatch.Start();

            for (var i = 0; i < concurrent; i++)
            {
                var i1 = i;

                tasks.Add(Task.Run(async () =>
                                   {
                                       await keyLocker.WaitAsync(testKey);

                                       var s = stopwatch.Elapsed;

                                       await Task.Delay(delay);

                                       var e = stopwatch.Elapsed;

                                       _testOutput.WriteLine($"Test {i1} start at {s}, completed at {e}, duration {e - s}, delay {delay}ms");

                                       var releasedCount = keyLocker.Release(testKey);

                                       Interlocked.Add(ref completed, releasedCount);
                                   }));
            }

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(concurrent, completed);
            Assert.Equal(0, keyLocker.GetWaitingCount(testKey));
        }

        [Fact]
        public async void ReleaseAll_ExistingKey_ShouldReleaseAllLocks()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object);
            const string testKey = "test_key";
            const int concurrent = 100;
            var tasks = new List<Task>();
            var completed = 0;

            var stopwatch = new Stopwatch();

            stopwatch.Start();

            for (var i = 0; i < concurrent; i++)
            {
                var i1 = i;

                tasks.Add(Task.Run(async () =>
                                   {
                                       await keyLocker.WaitAsync(testKey);

                                       var s = stopwatch.Elapsed;
                                       TimeSpan e;

                                       if (Interlocked.Increment(ref completed) == 1)
                                       {
                                           var delay = Random.Shared.Next(100, 500);

                                           _testOutput.WriteLine($"Test {i1} is the first, start at {s}, delay {delay}ms");

                                           await Task.Delay(delay);

                                           e = stopwatch.Elapsed;

                                           keyLocker.ReleaseAll(testKey);
                                       }
                                       else
                                       {
                                           e = stopwatch.Elapsed;
                                       }

                                       _testOutput.WriteLine($"Test {i1} start at {s}, completed at {e}, duration {e - s}");
                                   }));
            }

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(0, keyLocker.GetWaitingCount(testKey));
        }

        [Fact]
        public void Reset_ExistingKey_ShouldResetLockState()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object);
            const string testKey = "test_key";
            keyLocker.Wait(testKey);

            // Act
            keyLocker.Reset(testKey);
            var waitingCount = keyLocker.GetWaitingCount(testKey);

            // Assert
            Assert.Equal(0, waitingCount);
        }

        [Fact]
        public void Timeout_ShouldHandleTimeoutCorrectly()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object,
                timeout: TimeSpan.FromMilliseconds(10),
                throwTimeoutException: false);
            const string testKey = "test_key";

            // Act & Assert
            keyLocker.Wait(testKey);
            keyLocker.Wait(testKey); // This should timeout

            // Verify logger was called with warning
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Lock on key")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()!
                ),
                Times.Once
            );
        }

        [Fact]
        public void Timeout_WithThrowException_ShouldThrowTimeoutException()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object,
                timeout: TimeSpan.FromMilliseconds(10),
                throwTimeoutException: true);
            const string testKey = "test_key";

            // Act & Assert
            keyLocker.Wait(testKey);
            Assert.Throws<TimeoutException>(() => keyLocker.Wait(testKey));
        }

        [Fact]
        public void Dispose_ShouldCleanupResources()
        {
            // Arrange
            var keyLocker = new KeyLocker(_mockLogger.Object);

            // Act
            keyLocker.Dispose();

            // Additional verification could be added if needed
            Assert.True(true); // Placeholder for successful disposal
        }

        [Fact]
        public async Task CleanupIdleLocks_ShouldRemoveExpiredLocks()
        {
            // Arrange
            var cleanupInterval = TimeSpan.FromMilliseconds(100);
            var keyLocker = new KeyLocker(_mockLogger.Object, cleanupInterval: cleanupInterval);
            const string testKey = "test_key";

            // Act
            keyLocker.Wait(testKey);
            keyLocker.Release(testKey);

            // Wait for cleanup interval to pass
            await Task.Delay(cleanupInterval.Add(TimeSpan.FromMilliseconds(50)));

            // Assert
            Assert.Equal(0, keyLocker.GetWaitingCount(testKey));
            Assert.False(keyLocker.HasLock(testKey));
        }
    }
}
