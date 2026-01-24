using CreatioHelper.Application.Interfaces;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync;

/// <summary>
/// Test implementation of IConnectionLifecycle for testing state transitions.
/// </summary>
public class TestConnectionLifecycle : IConnectionLifecycle
{
    private ConnectionState _state = ConnectionState.Disconnected;
    private readonly object _stateLock = new();
    private DateTime _lastActivity = DateTime.UtcNow;
    private int _errorCount;

    public event EventHandler<ConnectionStateEventArgs>? StateChanged;

    public ConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public void SetState(ConnectionState newState, string? reason = null)
    {
        ConnectionState oldState;
        lock (_stateLock)
        {
            if (_state == newState) return;
            oldState = _state;
            _state = newState;
        }

        StateChanged?.Invoke(this, new ConnectionStateEventArgs
        {
            OldState = oldState,
            NewState = newState,
            Reason = reason,
            DeviceId = "test-device"
        });
    }

    public void RecordActivity()
    {
        _lastActivity = DateTime.UtcNow;
    }

    public void IncrementErrors()
    {
        Interlocked.Increment(ref _errorCount);
    }

    public void SetErrorCount(int count)
    {
        _errorCount = count;
    }

    public void SetLastActivity(DateTime time)
    {
        _lastActivity = time;
    }

    public ConnectionHealth GetHealth()
    {
        double score = 100.0;

        // Deduct for errors (up to 50 points)
        if (_errorCount > 0)
        {
            score -= Math.Min(50, _errorCount * 10);
        }

        // Deduct for inactivity (up to 30 points)
        var inactivitySeconds = (DateTime.UtcNow - _lastActivity).TotalSeconds;
        if (inactivitySeconds > 60)
        {
            score -= Math.Min(30, (inactivitySeconds - 60) / 10);
        }

        // Ensure score is within bounds
        score = Math.Max(0, Math.Min(100, score));

        return new ConnectionHealth
        {
            Score = score,
            Latency = TimeSpan.Zero,
            LastActivity = _lastActivity,
            BytesSent = 0,
            BytesReceived = 0,
            ErrorCount = _errorCount
        };
    }
}

public class ConnectionLifecycleTests
{
    [Fact]
    public void ConnectionState_InitialState_IsDisconnected()
    {
        // Arrange & Act
        var connection = new TestConnectionLifecycle();

        // Assert
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public void ConnectionState_AllStatesExist()
    {
        // Verify all expected states exist in the enum
        var states = Enum.GetValues<ConnectionState>();

        Assert.Contains(ConnectionState.Disconnected, states);
        Assert.Contains(ConnectionState.Connecting, states);
        Assert.Contains(ConnectionState.Connected, states);
        Assert.Contains(ConnectionState.Disconnecting, states);
        Assert.Contains(ConnectionState.Failed, states);
    }

    [Fact]
    public void Connection_TracksStateTransitions()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        var states = new List<ConnectionState>();

        connection.StateChanged += (sender, e) =>
        {
            states.Add(e.NewState);
        };

        // Act - simulate full connection lifecycle
        connection.SetState(ConnectionState.Connecting);
        connection.SetState(ConnectionState.Connected);
        connection.SetState(ConnectionState.Disconnecting);
        connection.SetState(ConnectionState.Disconnected);

        // Assert
        Assert.Equal(new[] {
            ConnectionState.Connecting,
            ConnectionState.Connected,
            ConnectionState.Disconnecting,
            ConnectionState.Disconnected
        }, states);
    }

    [Fact]
    public void Connection_StateChanged_IncludesOldAndNewState()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        ConnectionStateEventArgs? receivedArgs = null;

        connection.StateChanged += (sender, e) =>
        {
            receivedArgs = e;
        };

        // Act
        connection.SetState(ConnectionState.Connecting);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal(ConnectionState.Disconnected, receivedArgs.OldState);
        Assert.Equal(ConnectionState.Connecting, receivedArgs.NewState);
    }

    [Fact]
    public void Connection_StateChanged_IncludesDeviceId()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        ConnectionStateEventArgs? receivedArgs = null;

        connection.StateChanged += (sender, e) =>
        {
            receivedArgs = e;
        };

        // Act
        connection.SetState(ConnectionState.Connecting);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal("test-device", receivedArgs.DeviceId);
    }

    [Fact]
    public void Connection_StateChanged_IncludesReason()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        ConnectionStateEventArgs? receivedArgs = null;

        connection.StateChanged += (sender, e) =>
        {
            receivedArgs = e;
        };

        // Act
        connection.SetState(ConnectionState.Failed, "Connection timeout");

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal("Connection timeout", receivedArgs.Reason);
    }

    [Fact]
    public void Connection_SameState_DoesNotFireEvent()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        connection.SetState(ConnectionState.Connected);

        var eventCount = 0;
        connection.StateChanged += (sender, e) =>
        {
            eventCount++;
        };

        // Act - set same state again
        connection.SetState(ConnectionState.Connected);

        // Assert
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void Connection_FailedState_IsReachable()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        var states = new List<ConnectionState>();

        connection.StateChanged += (sender, e) =>
        {
            states.Add(e.NewState);
        };

        // Act - simulate connection failure
        connection.SetState(ConnectionState.Connecting);
        connection.SetState(ConnectionState.Failed);

        // Assert
        Assert.Equal(new[] {
            ConnectionState.Connecting,
            ConnectionState.Failed
        }, states);
        Assert.Equal(ConnectionState.Failed, connection.State);
    }

    [Fact]
    public void GetHealth_ReturnsValidScore()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();

        // Act
        var health = connection.GetHealth();

        // Assert
        Assert.InRange(health.Score, 0, 100);
    }

    [Fact]
    public void GetHealth_NewConnection_ReturnsHighScore()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();

        // Act
        var health = connection.GetHealth();

        // Assert - new connection with no errors should have high score
        Assert.Equal(100, health.Score);
    }

    [Fact]
    public void GetHealth_DecreasesWithErrors()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        var initialHealth = connection.GetHealth();

        // Act
        connection.IncrementErrors();
        var healthAfterError = connection.GetHealth();

        // Assert
        Assert.True(healthAfterError.Score < initialHealth.Score);
        Assert.Equal(1, healthAfterError.ErrorCount);
    }

    [Fact]
    public void GetHealth_MultipleErrors_DeductPoints()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();

        // Act
        connection.SetErrorCount(3);
        var health = connection.GetHealth();

        // Assert - 3 errors should deduct 30 points (10 per error)
        Assert.Equal(70, health.Score);
        Assert.Equal(3, health.ErrorCount);
    }

    [Fact]
    public void GetHealth_ErrorsCappedAt50Points()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();

        // Act - set many errors
        connection.SetErrorCount(10);
        var health = connection.GetHealth();

        // Assert - error deduction capped at 50
        Assert.Equal(50, health.Score);
    }

    [Fact]
    public void GetHealth_TracksLastActivity()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        var now = DateTime.UtcNow;
        connection.SetLastActivity(now);

        // Act
        var health = connection.GetHealth();

        // Assert
        Assert.Equal(now, health.LastActivity);
    }

    [Fact]
    public void GetHealth_InactivityReducesScore()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        var oldTime = DateTime.UtcNow.AddSeconds(-120); // 120 seconds ago
        connection.SetLastActivity(oldTime);

        // Act
        var health = connection.GetHealth();

        // Assert - 60 seconds of inactivity allowed, then deduction starts
        // After 120 seconds, 60 seconds past threshold, should deduct ~6 points
        Assert.True(health.Score < 100);
        Assert.True(health.Score >= 90); // Shouldn't deduct too much
    }

    [Fact]
    public void GetHealth_ScoreNeverBelowZero()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();
        connection.SetErrorCount(100); // Many errors
        connection.SetLastActivity(DateTime.UtcNow.AddHours(-1)); // Very old activity

        // Act
        var health = connection.GetHealth();

        // Assert
        Assert.True(health.Score >= 0);
    }

    [Fact]
    public void GetHealth_ScoreNeverAbove100()
    {
        // Arrange
        var connection = new TestConnectionLifecycle();

        // Act
        var health = connection.GetHealth();

        // Assert
        Assert.True(health.Score <= 100);
    }

    [Fact]
    public void ConnectionStateEventArgs_PropertiesSettable()
    {
        // Arrange & Act
        var args = new ConnectionStateEventArgs
        {
            OldState = ConnectionState.Disconnected,
            NewState = ConnectionState.Connected,
            Reason = "Test reason",
            DeviceId = "device-123"
        };

        // Assert
        Assert.Equal(ConnectionState.Disconnected, args.OldState);
        Assert.Equal(ConnectionState.Connected, args.NewState);
        Assert.Equal("Test reason", args.Reason);
        Assert.Equal("device-123", args.DeviceId);
    }

    [Fact]
    public void ConnectionHealth_AllPropertiesSettable()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var health = new ConnectionHealth
        {
            Score = 85.5,
            Latency = TimeSpan.FromMilliseconds(50),
            LastActivity = now,
            BytesSent = 1024,
            BytesReceived = 2048,
            ErrorCount = 2
        };

        // Assert
        Assert.Equal(85.5, health.Score);
        Assert.Equal(TimeSpan.FromMilliseconds(50), health.Latency);
        Assert.Equal(now, health.LastActivity);
        Assert.Equal(1024, health.BytesSent);
        Assert.Equal(2048, health.BytesReceived);
        Assert.Equal(2, health.ErrorCount);
    }
}

public class ConnectionLifecycleInterfaceTests
{
    [Fact]
    public void IConnectionLifecycle_CanBeMocked()
    {
        // Arrange
        var mock = new Mock<IConnectionLifecycle>();
        mock.Setup(c => c.State).Returns(ConnectionState.Connected);
        mock.Setup(c => c.GetHealth()).Returns(new ConnectionHealth { Score = 95 });

        // Act
        var state = mock.Object.State;
        var health = mock.Object.GetHealth();

        // Assert
        Assert.Equal(ConnectionState.Connected, state);
        Assert.Equal(95, health.Score);
    }

    [Fact]
    public void IConnectionLifecycle_MockedEventCanBeRaised()
    {
        // Arrange
        var mock = new Mock<IConnectionLifecycle>();
        ConnectionStateEventArgs? receivedArgs = null;

        mock.Object.StateChanged += (sender, e) => receivedArgs = e;

        // Act
        mock.Raise(c => c.StateChanged += null,
            mock.Object,
            new ConnectionStateEventArgs
            {
                OldState = ConnectionState.Disconnected,
                NewState = ConnectionState.Connecting
            });

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal(ConnectionState.Disconnected, receivedArgs.OldState);
        Assert.Equal(ConnectionState.Connecting, receivedArgs.NewState);
    }
}
