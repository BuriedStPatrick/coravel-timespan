using System;
using System.Threading;
using System.Threading.Tasks;
using Coravel.Invocable;
using Coravel.Scheduling.Schedule.Cron;
using Coravel.Scheduling.Schedule.Interfaces;
using Coravel.Tasks;
using Coravel.Scheduling.Schedule.Zoned;
using Microsoft.Extensions.DependencyInjection;

namespace Coravel.Scheduling.Schedule.Event
{
    public class ScheduledEvent : IScheduleInterval, IScheduledEventConfiguration
    {
        private CronExpression _expression;
        private ActionOrAsyncFunc _scheduledAction;
        private Type _invocableType = null;
        private bool _preventOverlapping = false;
        private string _eventUniqueId = null;
        private IServiceScopeFactory _scopeFactory;
        private Func<Task<bool>> _whenPredicate;
        private bool _isScheduledFromTimeSpan = false;
        private TimeSpan? _timeSpanInterval = null;
        private object[] _constructorParameters = null;
        private ZonedTime _zonedTime = ZonedTime.AsUTC();
        private bool _runOnceAtStart = false;
        private bool _runOnce = false;
        private bool _wasPreviouslyRun = false;

        public ScheduledEvent(Action scheduledAction, IServiceScopeFactory scopeFactory) : this(scopeFactory)
        {
            this._scheduledAction = new ActionOrAsyncFunc(scheduledAction);
        }

        public ScheduledEvent(Func<Task> scheduledAsyncTask, IServiceScopeFactory scopeFactory) : this(scopeFactory)
        {
            this._scheduledAction = new ActionOrAsyncFunc(scheduledAsyncTask);
        }

        private ScheduledEvent(IServiceScopeFactory scopeFactory)
        {
            this._scopeFactory = scopeFactory;
            this._eventUniqueId = Guid.NewGuid().ToString();
        }

        public static ScheduledEvent WithInvocable<T>(IServiceScopeFactory scopeFactory) where T : IInvocable
        {
            return WithInvocableType(typeof(T), scopeFactory);
        }

        internal static ScheduledEvent WithInvocableAndParams<T>(IServiceScopeFactory scopeFactory, object[] parameters)
            where T : IInvocable
        {
            var scheduledEvent = WithInvocableType(typeof(T), scopeFactory);
            scheduledEvent._constructorParameters = parameters;
            return scheduledEvent;
        }

        internal static ScheduledEvent WithInvocableAndParams(Type invocableType, IServiceScopeFactory scopeFactory, object[] parameters)
        {
            if (!typeof(IInvocable).IsAssignableFrom(invocableType))
            {
                throw new ArgumentException(
                    $"When using {nameof(IScheduler.ScheduleWithParams)}() you must supply a type that inherits from {nameof(IInvocable)}.",
                    nameof(invocableType));
            }

            var scheduledEvent = WithInvocableType(invocableType, scopeFactory);
            scheduledEvent._constructorParameters = parameters;
            return scheduledEvent;
        }

        public static ScheduledEvent WithInvocableType(Type invocableType, IServiceScopeFactory scopeFactory)
        {
            var scheduledEvent = new ScheduledEvent(scopeFactory);
            scheduledEvent._invocableType = invocableType;
            return scheduledEvent;
        }

        private static readonly int _OneMinuteAsSeconds = 60;

        public bool IsDue(DateTime utcNow)
        {
            var zonedNow = this._zonedTime.Convert(utcNow);

            if (this._isScheduledFromTimeSpan)
            {
                var isSecondDue = this.IsSecondsDue(zonedNow);
                var isWeekDayDue = this._expression.IsWeekDayDue(zonedNow);
                return isSecondDue && isWeekDayDue;
            }
            else
            {
                return this._expression.IsDue(zonedNow);
            }
        }

        public async Task InvokeScheduledEvent(CancellationToken cancellationToken)
        {
            if (await WhenPredicateFails())
            {
                return;
            }

            if (this._invocableType is null)
            {
                await this._scheduledAction.Invoke();
            }
            else
            {
                await using AsyncServiceScope scope = new(this._scopeFactory.CreateAsyncScope());
                if (GetInvocable(scope.ServiceProvider) is IInvocable invocable)
                {
                    if (invocable is ICancellableInvocable cancellableInvokable)
                    {
                        cancellableInvokable.CancellationToken = cancellationToken;
                    }

                    await invocable.Invoke();
                }
            }

            MarkedAsExecutedOnce();
            UnScheduleIfWarranted();
        }

        public bool ShouldPreventOverlapping() => this._preventOverlapping;

        public string OverlappingUniqueIdentifier() => this._eventUniqueId;

        public bool IsScheduledCronBasedTask() => !this._isScheduledFromTimeSpan;

        public IScheduledEventConfiguration Daily()
        {
            this._expression = new CronExpression("00 00 * * *");
            return this;
        }

        public IScheduledEventConfiguration DailyAtHour(int hour)
        {
            this._expression = new CronExpression($"00 {hour} * * *");
            return this;
        }

        public IScheduledEventConfiguration DailyAt(int hour, int minute)
        {
            this._expression = new CronExpression($"{minute} {hour} * * *");
            return this;
        }

        public IScheduledEventConfiguration Hourly()
        {
            this._expression = new CronExpression($"00 * * * *");
            return this;
        }

        public IScheduledEventConfiguration HourlyAt(int minute)
        {
            this._expression = new CronExpression($"{minute} * * * *");
            return this;
        }

        public IScheduledEventConfiguration EveryMinute()
        {
            this._expression = new CronExpression($"* * * * *");
            return this;
        }

        public IScheduledEventConfiguration EveryFiveMinutes()
        {
            this._expression = new CronExpression($"*/5 * * * *");
            return this;
        }

        public IScheduledEventConfiguration EveryTenMinutes()
        {
            // todo fix "*/10" in cron part
            this._expression = new CronExpression($"*/10 * * * *");
            return this;
        }

        public IScheduledEventConfiguration EveryFifteenMinutes()
        {
            this._expression = new CronExpression($"*/15 * * * *");
            return this;
        }

        public IScheduledEventConfiguration EveryThirtyMinutes()
        {
            this._expression = new CronExpression($"*/30 * * * *");
            return this;
        }

        public IScheduledEventConfiguration Weekly()
        {
            this._expression = new CronExpression($"00 00 * * 1");
            return this;
        }

        public IScheduledEventConfiguration Monthly()
        {
            this._expression = new CronExpression($"00 00 1 * *");
            return this;
        }

        public IScheduledEventConfiguration Cron(string cronExpression)
        {
            this._expression = new CronExpression(cronExpression);
            return this;
        }

        public IScheduledEventConfiguration Monday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Monday);
            return this;
        }

        public IScheduledEventConfiguration Tuesday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Tuesday);
            return this;
        }

        public IScheduledEventConfiguration Wednesday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Wednesday);
            return this;
        }

        public IScheduledEventConfiguration Thursday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Thursday);
            return this;
        }

        public IScheduledEventConfiguration Friday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Friday);
            return this;
        }

        public IScheduledEventConfiguration Saturday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Saturday);
            return this;
        }

        public IScheduledEventConfiguration Sunday()
        {
            this._expression.AppendWeekDay(DayOfWeek.Sunday);
            return this;
        }

        public IScheduledEventConfiguration Weekday()
        {
            this.Monday()
                .Tuesday()
                .Wednesday()
                .Thursday()
                .Friday();
            return this;
        }

        public IScheduledEventConfiguration Weekend()
        {
            this.Saturday()
                .Sunday();
            return this;
        }

        public IScheduledEventConfiguration PreventOverlapping(string uniqueIdentifier)
        {
            this._preventOverlapping = true;
            return this.AssignUniqueIndentifier(uniqueIdentifier);
        }

        public IScheduledEventConfiguration When(Func<Task<bool>> predicate)
        {
            this._whenPredicate = predicate;
            return this;
        }

        public IScheduledEventConfiguration AssignUniqueIndentifier(string uniqueIdentifier)
        {
            this._eventUniqueId = uniqueIdentifier;
            return this;
        }

        public Type InvocableType() => this._invocableType;

        private async Task<bool> WhenPredicateFails()
        {
            return this._whenPredicate != null && (!await _whenPredicate.Invoke());
        }

        public IScheduledEventConfiguration EverySecond() =>
            EveryInterval(TimeSpan.FromSeconds(1));

        public IScheduledEventConfiguration EveryFiveSeconds() =>
            EveryInterval(TimeSpan.FromSeconds(5));

        public IScheduledEventConfiguration EveryTenSeconds() =>
            EveryInterval(TimeSpan.FromSeconds(10));

        public IScheduledEventConfiguration EveryFifteenSeconds() =>
            EveryInterval(TimeSpan.FromSeconds(15));

        public IScheduledEventConfiguration EveryThirtySeconds() => 
            EveryInterval(TimeSpan.FromSeconds(30));

        public IScheduledEventConfiguration EverySeconds(int seconds) =>
            EveryInterval(TimeSpan.FromSeconds(seconds));

        public IScheduledEventConfiguration EveryInterval(TimeSpan timeSpan)
        {
            this._timeSpanInterval = timeSpan;
            this._isScheduledFromTimeSpan = true;
            this._expression = new CronExpression("* * * * *");

            return this;
        }

        public IScheduledEventConfiguration Zoned(TimeZoneInfo timeZoneInfo)
        {
            this._zonedTime = new ZonedTime(timeZoneInfo);
            return this;
        }

        public IScheduledEventConfiguration RunOnceAtStart()
        {
            this._runOnceAtStart = true;
            return this;
        }
        
        public IScheduledEventConfiguration Once()
        {
            this._runOnce = true;
            return this;
        }
        
        private bool IsSecondsDue(DateTime utcNow)
        {
            // TODO: Potentially breaking change:
            // Changes assumption is that TimeSpan interval (previously _secondsInterval) was not null here which seemed dangerous.
            if (this._timeSpanInterval is null)
            {
                return false;
            }

            var seconds = this._timeSpanInterval.Value.Seconds;

            if (utcNow.Second == 0)
            {
                return seconds == 0 || _OneMinuteAsSeconds % seconds == 0;
            }
            else
            {
                return seconds != 0 && utcNow.Second % seconds == 0;
            }
        }

        internal bool ShouldRunOnceAtStart() => this._runOnceAtStart;
        
        private object GetInvocable(IServiceProvider serviceProvider)
        {
            if (this._constructorParameters?.Length > 0)
            {
                return ActivatorUtilities.CreateInstance(serviceProvider, this._invocableType,
                    this._constructorParameters);
            }

            return serviceProvider.GetRequiredService(this._invocableType);
        }

        private bool PreviouslyRanAndMarkedToRunOnlyOnce() => this._runOnce && this._wasPreviouslyRun;
        
        private void MarkedAsExecutedOnce()
        {
            this._wasPreviouslyRun = true;
        }
        
        private void UnScheduleIfWarranted()
        {
            if (PreviouslyRanAndMarkedToRunOnlyOnce())
            {
                using var scope = this._scopeFactory.CreateScope();
                var scheduler = scope.ServiceProvider.GetService<IScheduler>() as Scheduler;
                scheduler.TryUnschedule(this._eventUniqueId);
            }
        }
    }
}
