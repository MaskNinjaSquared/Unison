// =============================================================================
// Tests for ContactDisplayNames.
//
// The name shown for an address, read from the shared tables and the person
// cache. The ranking itself is DisplayNameResolution's; these pin what is fed
// into it and how the account's own address is named.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ContactDisplayNamesTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string AnaLid = "123456789012345@lid";
        private const string Me = "5511911112222@s.whatsapp.net";
        private const string MeLid = "999999999999999@lid";

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly DisplayNameTable _pushNames = new DisplayNameTable();
        private readonly DisplayNameTable _addressBook = new DisplayNameTable();
        private readonly FakePeople _people = new FakePeople();
        private SelfNameInputs _self = new SelfNameInputs { Id = Me, Lid = MeLid, Phone = "5511911112222" };

        private ContactDisplayNames Build()
        {
            _aliases.BindSelf(() => _self.Id, () => _self.Lid);
            var names = new ContactDisplayNames(_aliases, _pushNames, _addressBook, _people, new FakeMarkers());
            names.Bind(() => _self, null);
            return names;
        }

        // --- Contacts ----------------------------------------------------------

        [Fact]
        public void The_address_book_beats_a_name_the_store_only_observed()
        {
            _people.Cache(Ana, "aninha", PersonSource.Observed);
            _addressBook.Set(Ana, "Ana Trabalho");

            Assert.Equal("Ana Trabalho", Build().Resolve(Ana, "chat"));
        }

        [Fact]
        public void A_stored_address_book_name_is_used_as_is()
        {
            _people.Cache(Ana, "Ana Paula", PersonSource.AddressBook);
            _addressBook.Set(Ana, "Ana Trabalho");

            Assert.Equal("Ana Paula", Build().Resolve(Ana, "chat"));
        }

        [Fact]
        public void A_lid_finds_the_contact_saved_under_its_phone_number()
        {
            _aliases[AnaLid] = Ana;
            _addressBook.Set(Ana, "Ana Trabalho");

            Assert.Equal("Ana Trabalho", Build().Resolve(AnaLid, "chat"));
        }

        [Fact]
        public void A_push_name_is_marked_as_unsaved()
        {
            _pushNames.Set(Ana, "aninha");

            Assert.Equal("~aninha", Build().Resolve(Ana, "chat"));
        }

        [Fact]
        public void With_nothing_known_the_number_is_shown()
        {
            Assert.Equal("5511988887777", Build().Resolve(Ana, "chat"));
        }

        [Fact]
        public void A_cold_address_is_loaded_into_the_cache_for_next_time()
        {
            Build().Resolve(Ana, "chat");

            Assert.Contains(Ana, _people.Loaded);
        }

        [Fact]
        public void Blank_resolves_to_blank()
        {
            Assert.Equal(string.Empty, Build().Resolve(null, "chat"));
            Assert.Equal(string.Empty, Build().Resolve("", "chat"));
        }

        // --- The account itself -------------------------------------------------

        [Fact]
        public void The_account_is_named_from_its_credentials()
        {
            _self.CredentialsName = "Marcos";

            Assert.Equal("Marcos", Build().Resolve(Me, "chat"));
        }

        [Fact]
        public void The_profile_name_beats_a_push_name_on_the_self_chat()
        {
            _self.CurrentUserName = "Marcos Profile";
            _pushNames.Set(Me, "RandomWaNick");

            Assert.Equal("Marcos Profile", Build().Resolve(Me, "chat"));
        }

        [Fact]
        public void The_profile_name_beats_an_address_book_entry_on_the_self_chat()
        {
            _self.CurrentUserName = "Marcos Profile";
            _addressBook.Set(Me, "Agenda Me");

            Assert.Equal("Marcos Profile", Build().Resolve(Me, "chat"));
        }

        [Fact]
        public void Without_a_profile_name_the_push_name_still_names_the_self_chat()
        {
            _pushNames.Set(Me, "WaNick");

            Assert.Equal("WaNick", Build().Resolve(Me, "chat"));
        }

        [Fact]
        public void The_account_falls_back_to_its_number()
        {
            Assert.Equal("5511911112222", Build().Resolve(MeLid, "chat"));
        }

        [Fact]
        public void Before_binding_the_account_is_not_recognized_and_nothing_throws()
        {
            var names = new ContactDisplayNames(_aliases, _pushNames, _addressBook, _people, new FakeMarkers());

            Assert.Equal("5511988887777", names.Resolve(Ana, "chat"));
        }

        // --- Fakes -------------------------------------------------------------

        private sealed class FakeMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => false;

            public string StripMarker(string label) => label;
        }

        private sealed class FakePeople : IPersonStore
        {
            private readonly Dictionary<string, Person> _cache = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);

            public List<string> Loaded { get; } = new List<string>();

            public event EventHandler<string>? PersonChanged { add { } remove { } }

            public void Cache(string jid, string name, PersonSource source) =>
                _cache[jid] = new Person { Jid = jid, Name = name, Source = source };

            public Person? TryGetCached(string jid) => _cache.TryGetValue(jid, out var p) ? p : null;

            public Task<Person?> GetAsync(string jid)
            {
                Loaded.Add(jid);
                return Task.FromResult(TryGetCached(jid));
            }

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<bool> UpsertIfChangedAsync(string jid, string name, string avatarUrl, string phone, PersonSource source) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits) =>
                Task.FromResult<IReadOnlyList<Person>>(new List<Person>());

            public Task<IReadOnlyList<Person>> ListWithPhoneAsync() =>
                Task.FromResult<IReadOnlyList<Person>>(new List<Person>());

            public Task ReplaceGroupMembershipsAsync(string groupJid, IReadOnlyList<PersonGroupMembership> members) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid) =>
                Task.FromResult<IReadOnlyList<PersonGroupMembership>>(new List<PersonGroupMembership>());
        }
    }
}
