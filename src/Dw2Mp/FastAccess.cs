using System.Linq.Expressions;
using System.Reflection;

namespace Dw2Mp;

/// <summary>
/// Compiled reflection: field reads and list indexing as delegates instead of
/// MethodInfo.Invoke / FieldInfo.GetValue.
///
/// WHY. The delta builder walks every research project of every empire on every delta --
/// ~10,300 objects, each costing one indexer Invoke plus three GetValue calls, so roughly
/// 41,000 reflection calls three times a second on the SIMULATION thread. Measured with
/// per-section timing, that was 3.97 ms of a 4.55 ms delta build: 87% of the cost, for 3%
/// of the records actually sent.
///
/// WHY NOT JUST WALK LESS. ResearchSystem also exposes ResearchQueue and NextProjects, and
/// walking those would be fast. It would also be a guess: a project whose state changes
/// outside the queue would be missed silently, and an under-report is indistinguishable
/// from "nothing changed". Compiling the accessors keeps the walk exhaustive and removes
/// the cost rather than moving it, so there is no correctness question to get wrong.
///
/// Everything is cached by (type, member) and compiled once. Compilation is not cheap --
/// it is paid once per member for the life of the process, against tens of thousands of
/// calls per second.
///
/// This is a HOST-side optimisation and touches nothing on the wire. It cannot change what
/// a delta contains, only how quickly the host works out what to put in one.
/// </summary>
internal static class FastAccess
{
    private static readonly Dictionary<(Type, string), object> _getters = new();
    private static readonly Dictionary<Type, Func<object, int, object>> _indexers = new();

    /// <summary>
    /// A compiled reader for one field. Returns null when the field does not exist, so
    /// callers keep the same "member missing" branch they had with reflection.
    /// </summary>
    public static Func<object, T> Getter<T>(Type owner, string fieldName)
    {
        if (owner is null) return null;

        lock (_getters)
        {
            var key = (owner, fieldName);
            if (_getters.TryGetValue(key, out var cached)) return (Func<object, T>)cached;

            Func<object, T> made = null;

            try
            {
                var field = owner.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (field is not null)
                {
                    var instance = Expression.Parameter(typeof(object), "o");
                    Expression access = Expression.Field(
                        Expression.Convert(instance, field.DeclaringType!), field);

                    // Convert covers the widening cases (short -> long for an id) as well as
                    // the identity case; without it a mismatched T is a runtime surprise
                    // rather than a compile-time one.
                    if (field.FieldType != typeof(T))
                        access = Expression.Convert(access, typeof(T));

                    made = Expression.Lambda<Func<object, T>>(access, instance).Compile();
                }
            }
            catch
            {
                made = null;
            }

            _getters[key] = made;
            return made;
        }
    }

    /// <summary>A compiled call to the list's get_Item(int).</summary>
    public static Func<object, int, object> Indexer(Type listType)
    {
        if (listType is null) return null;

        lock (_indexers)
        {
            if (_indexers.TryGetValue(listType, out var cached)) return cached;

            Func<object, int, object> made = null;

            try
            {
                var getItem = listType.GetMethod("get_Item", new[] { typeof(int) });

                if (getItem is not null)
                {
                    var instance = Expression.Parameter(typeof(object), "o");
                    var index = Expression.Parameter(typeof(int), "i");

                    Expression call = Expression.Call(
                        Expression.Convert(instance, getItem.DeclaringType!), getItem, index);

                    if (call.Type.IsValueType) call = Expression.Convert(call, typeof(object));

                    made = Expression.Lambda<Func<object, int, object>>(call, instance, index).Compile();
                }
            }
            catch
            {
                made = null;
            }

            _indexers[listType] = made;
            return made;
        }
    }
}
