using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using OfflineStore.Services;

namespace OfflineStore
{
    public partial class MainWindow
    {
        private EmployeeNote _currentNote;
        private bool _loadingNote;
        private bool _noteDirty;
        private bool _acceptingSuggestion;

        // Suggestions are optional; no match is forced into the product-name field.
        private void ProductNameTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_vm == null || _acceptingSuggestion) return;
            var typed = ProductNameInput.Text.Trim();
            if (typed.Length < 2)
            {
                ProductSuggestionPopup.IsOpen = false;
                return;
            }
            var names = _vm.Products.Select(p => p.Name)
                .Where(n => n.IndexOf(typed, StringComparison.CurrentCultureIgnoreCase) >= 0
                    && !string.Equals(n, typed, StringComparison.CurrentCultureIgnoreCase))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(n => n.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(n => n).Take(6).ToList();
            ProductSuggestionList.ItemsSource = names;
            ProductSuggestionList.SelectedIndex = names.Count > 0 ? 0 : -1;
            ProductSuggestionPopup.IsOpen = names.Count > 0;
        }
        private void ChooseProductSuggestion()
        {
            var item = ProductSuggestionList.SelectedItem as string;
            if (string.IsNullOrEmpty(item)) return;
            _acceptingSuggestion = true;
            try
            {
                ProductNameInput.Text = item;
                ProductNameInput.CaretIndex = item.Length;
                ProductSuggestionPopup.IsOpen = false;
                ProductNameInput.Focus();
            }
            finally { _acceptingSuggestion = false; }
        }
        private void ProductNameKeyDown(object sender, KeyEventArgs e)
        {
            if (!ProductSuggestionPopup.IsOpen) return;
            if (e.Key == Key.Enter)
            {
                ChooseProductSuggestion();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ProductSuggestionPopup.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                ProductSuggestionList.SelectedIndex = Math.Min(
                    ProductSuggestionList.Items.Count - 1, ProductSuggestionList.SelectedIndex + 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                ProductSuggestionList.SelectedIndex = Math.Max(0, ProductSuggestionList.SelectedIndex - 1);
                e.Handled = true;
            }
        }
        private void ProductSuggestionMouseUp(object sender, MouseButtonEventArgs e)
        { ChooseProductSuggestion(); }
        private void ProductSuggestionDoubleClick(object sender, MouseButtonEventArgs e)
        { ChooseProductSuggestion(); }

        private void RefreshNotes(long selectId = 0)
        {
            var items = _service.Notes.ListAll();
            var filter = NotesSearchBox == null ? "" : NotesSearchBox.Text.Trim();
            if (filter.Length > 0)
                items = items.Where(n =>
                    n.Title.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    n.Author.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    .ToList();
            _loadingNote = true;
            try
            {
                NotesListBox.ItemsSource = items;
                long chosenId = selectId != 0 ? selectId :
                    (_currentNote == null ? 0 : _currentNote.Id);
                if (chosenId != 0)
                    NotesListBox.SelectedItem = items.FirstOrDefault(n => n.Id == chosenId);
            }
            finally { _loadingNote = false; }
        }
        private void NotesSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingNote || NotesListBox == null) return;
            RefreshNotes();
        }
        private void NoteContentChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingNote || NotesEditor == null || NoteTitleBox == null || NoteStatusText == null) return;
            _noteDirty = true;
            NoteStatusText.Text = "هناك تغييرات لم تُحفظ بعد.";
        }
        private string CurrentNoteRtf()
        {
            var range = new TextRange(NotesEditor.Document.ContentStart, NotesEditor.Document.ContentEnd);
            using (var stream = new MemoryStream())
            {
                range.Save(stream, DataFormats.Rtf);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        private void SaveCurrentNote()
        {
            if (!_service.Can("VIEW")) throw new UnauthorizedAccessException("الجلسة لم تعد صالحة.");
            var title = NoteTitleBox.Text.Trim();
            var rtf = CurrentNoteRtf();
            long id;
            if (_currentNote == null)
                id = _service.Notes.Create(title, rtf);
            else
            {
                _service.Notes.Update(_currentNote.Id, title, rtf);
                id = _currentNote.Id;
            }
            _noteDirty = false;
            // A saved title may not match the current search. Clear the filter
            // before selecting the saved note; otherwise the next save could
            // incorrectly create a second note.
            if (NotesSearchBox != null && NotesSearchBox.Text.Length > 0)
            {
                _loadingNote = true;
                try { NotesSearchBox.Clear(); }
                finally { _loadingNote = false; }
            }
            RefreshNotes(id);
            _currentNote = NotesListBox.SelectedItem as EmployeeNote;
            if (_currentNote == null)
                throw new InvalidOperationException("تم الحفظ لكن تعذر عرض الملحوظة بعد تحديث القائمة.");
            NoteStatusText.Text = "تم حفظ الملحوظة داخل قاعدة بيانات المحل.";
        }
        private bool ConfirmNoteChanges()
        {
            if (!_noteDirty) return true;
            var answer = MessageBox.Show("هناك تعديلات غير محفوظة. هل تريد حفظها؟",
                "ملحوظات", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return true;
            try { SaveCurrentNote(); return true; }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تعذر الحفظ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        private void LoadNote(EmployeeNote note)
        {
            _loadingNote = true;
            try
            {
                _currentNote = note;
                NoteTitleBox.Text = note == null ? "" : note.Title;
                NotesEditor.Document = new FlowDocument { FlowDirection = FlowDirection.RightToLeft };
                if (note != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(note.Rtf);
                    using (var stream = new MemoryStream(bytes))
                        new TextRange(NotesEditor.Document.ContentStart, NotesEditor.Document.ContentEnd)
                            .Load(stream, DataFormats.Rtf);
                }
                bool writable = note == null || note.AuthorId == _service.CurrentUser.Id
                    || _service.Can("USERS");
                NotesEditor.IsReadOnly = !writable;
                NoteTitleBox.IsReadOnly = !writable;
                _noteDirty = false;
                NoteStatusText.Text = note == null ? "اكتب عنوانًا ونصًا ثم اضغط حفظ."
                    : (writable ? "الملحوظة محفوظة. يمكنك تعديلها." : "هذه الملحوظة للقراءة فقط.");
            }
            finally { _loadingNote = false; }
        }
        private void NewNoteClick(object sender, RoutedEventArgs e)
        {
            if (!ConfirmNoteChanges()) return;
            _loadingNote = true;
            try { NotesListBox.SelectedItem = null; }
            finally { _loadingNote = false; }
            LoadNote(null);
            NoteTitleBox.Focus();
        }
        private void NotesSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingNote || NotesEditor == null) return;
            var requested = NotesListBox.SelectedItem as EmployeeNote;
            if (requested == _currentNote) return;
            if (!ConfirmNoteChanges())
            {
                _loadingNote = true;
                try { NotesListBox.SelectedItem = _currentNote; }
                finally { _loadingNote = false; }
                return;
            }
            if (requested != null)
            {
                requested = NotesListBox.Items.Cast<EmployeeNote>()
                    .FirstOrDefault(n => n.Id == requested.Id) ?? requested;
                _loadingNote = true;
                try { NotesListBox.SelectedItem = requested; }
                finally { _loadingNote = false; }
            }
            try { LoadNote(requested); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تعذر فتح الملحوظة", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
                private void SaveNoteClick(object sender, RoutedEventArgs e)
        {
            try { SaveCurrentNote(); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تعذر حفظ الملحوظة", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

    }
}
