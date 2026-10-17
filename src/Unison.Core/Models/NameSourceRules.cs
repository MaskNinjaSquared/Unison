namespace Unison.Core.Models
{
    public static class NameSourceRules
    {
        /// <summary>True when a name from <paramref name="candidate"/> may take the place of one from <paramref name="current"/>.</summary>
        public static bool CanBeReplacedBy(this NameSource current, NameSource candidate)
        {
            return candidate >= current;
        }

        /// <summary>True for names the contact picked rather than the user saved; those are shown with a tilde.</summary>
        public static bool IsChosenByContact(this NameSource source)
        {
            return source == NameSource.PushName;
        }

        /// <summary>Only an address-book match is the user's own name; every other origin was learned.</summary>
        public static NameSource ToNameSource(this PersonSource source)
        {
            return source == PersonSource.AddressBook ? NameSource.AddressBook : NameSource.Learned;
        }
    }
}
