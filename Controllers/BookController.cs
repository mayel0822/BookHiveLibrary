using BookHiveLibrary.Data;
using BookHiveLibrary.Models;
using BookHiveLibrary.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using Microsoft.AspNetCore.Authorization;
using QRCoder;

namespace BookHiveLibrary.Controllers
{
    // This controller handles managing the library's book collection.
    // Both librarians and MIS staff can use it.
    // It covers: viewing the catalog, adding books, editing books, importing from Excel,
    // archiving old books, restoring them, printing shelf labels, and generating QR codes.
    [Authorize(Roles = "LIBRARIAN,MIS")]
    public class BookController : Controller
    {
        private readonly ApplicationDbContext _context;

        public BookController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Shows the library catalog — all active (non-archived) books.
        // Supports searching by title, author, or call number, and filtering by category.
        public async Task<IActionResult> Index(string? search, string? category)
        {
            var query = _context.Books.Where(book => !book.IsArchived);

            bool hasSearch   = !string.IsNullOrWhiteSpace(search);
            bool hasCategory = !string.IsNullOrWhiteSpace(category);

            if (hasSearch)
                query = query.Where(book =>
                    book.Title.Contains(search!)      ||
                    book.Author.Contains(search!)     ||
                    book.CallNumber.Contains(search!));

            if (hasCategory)
                query = query.Where(book => book.Category == category);

            var books = await query.OrderBy(book => book.Title).ToListAsync();

            var categories = await _context.Books
                .Where(book => !book.IsArchived)
                .Select(book => book.Category)
                .Distinct()
                .OrderBy(cat => cat)
                .ToListAsync();

            ViewBag.Search     = search;
            ViewBag.Category   = category;
            ViewBag.Categories = categories;

            // Stat cards — same rule on every book page:
            //   Total    = SUM(TotalQuantity) non-archived
            //   Borrowed = PickedUp or Overdue reservations only (Approved/Pending = still Available)
            //   Available = Total − Borrowed
            ViewBag.TotalBooks     = await _context.Books.Where(b => !b.IsArchived).SumAsync(b => (int?)b.TotalQuantity) ?? 0;
            ViewBag.BorrowedBooks  = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp" || r.Status == "Overdue");
            ViewBag.AvailableBooks = (int)ViewBag.TotalBooks - (int)ViewBag.BorrowedBooks;
            ViewBag.ArchivedBooks  = await _context.Books.CountAsync(b => b.IsArchived);

            return View(books);
        }

        // Shows the form for adding a new book to the library.
        // Also shows summary numbers at the top (total, available, borrowed, archived).
        public async Task<IActionResult> Register()
        {
            // Stat card rules (same on every page):
            //   Total    = SUM(TotalQuantity) of non-archived books
            //   Borrowed = reservations physically picked up and not yet returned (PickedUp or Overdue)
            //              Approved/Pending do NOT count — if student never picks up, it becomes Void and stays Available
            //   Available = Total − Borrowed  (derived, never from AvailableQuantity field)
            ViewBag.TotalBooks     = await _context.Books.Where(book => !book.IsArchived).SumAsync(book => (int?)book.TotalQuantity) ?? 0;
            ViewBag.BorrowedBooks  = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp" || r.Status == "Overdue");
            ViewBag.AvailableBooks = (int)ViewBag.TotalBooks - (int)ViewBag.BorrowedBooks;
            ViewBag.ArchivedBooks  = await _context.Books.CountAsync(book => book.IsArchived);
            return View(new BookFormViewModel());
        }

        // Saves a new book to the database.
        // Sets AvailableQuantity equal to TotalQuantity since no copies are borrowed yet.
        // If a cover image file was uploaded, it is saved to wwwroot/uploads/covers/.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(BookFormViewModel model, IFormFile? coverImageFile)
        {
            // Block exact-title duplicates (case-insensitive) among active books.
            // Archived books don't count — a librarian may legitimately re-add a
            // title that was previously archived.
            bool titleAlreadyExists = await _context.Books.AnyAsync(book =>
                !book.IsArchived && book.Title.ToLower() == model.Title.ToLower());
            if (titleAlreadyExists)
            {
                ModelState.AddModelError(nameof(model.Title),
                    $"\"{model.Title}\" has already been registered. Please add another book.");
            }

            if (!ModelState.IsValid) return View(model);

            // Save uploaded cover image file if provided
            string coverImageUrl = model.CoverImageUrl ?? "";
            if (coverImageFile != null && coverImageFile.Length > 0)
                coverImageUrl = await SaveCoverImageAsync(coverImageFile);

            var newBook = new Book
            {
                Title             = model.Title,
                Author            = model.Author,
                Category          = model.Category,
                GradeLevel        = model.GradeLevel,
                CallNumber        = model.CallNumber,
                AisleLocation     = model.AisleLocation,
                Description       = model.Description,
                CoverImageUrl     = coverImageUrl,
                PublishedYear     = model.PublishedYear,
                ISBN              = model.ISBN,
                TotalQuantity     = model.TotalQuantity,
                AvailableQuantity = model.TotalQuantity, // All copies are available when first added
                BookFor           = model.BookFor,
                IsRoomUseOnly     = model.IsRoomUseOnly
            };
            _context.Books.Add(newBook);

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book registered successfully.";
            return RedirectToAction("Index");
        }

        // Saves a cover image file to wwwroot/uploads/covers/ and returns its relative URL.
        // The file is renamed to a unique GUID to avoid name collisions.
        private async Task<string> SaveCoverImageAsync(IFormFile file)
        {
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "covers");
            Directory.CreateDirectory(uploadsFolder); // create folder if it doesn't exist yet

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var fileName  = Guid.NewGuid().ToString() + extension;
            var filePath  = Path.Combine(uploadsFolder, fileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);

            return "/uploads/covers/" + fileName;
        }

        // Shows the edit form for an existing book, already filled in with the current details.
        public async Task<IActionResult> Edit(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            var formModel = new BookFormViewModel
            {
                Id                = book.Id,
                Title             = book.Title,
                Author            = book.Author,
                Category          = book.Category,
                GradeLevel        = book.GradeLevel,
                CallNumber        = book.CallNumber,
                AisleLocation     = book.AisleLocation,
                Description       = book.Description,
                CoverImageUrl     = book.CoverImageUrl,
                PublishedYear     = book.PublishedYear,
                TotalQuantity     = book.TotalQuantity,
                ISBN              = book.ISBN,
                AvailableQuantity = book.AvailableQuantity,
                BookFor           = book.BookFor,
                IsRoomUseOnly     = book.IsRoomUseOnly
            };
            return View(formModel);
        }

        // Saves changes to an existing book.
        //
        // Important: Instead of trusting the number typed in the form for available copies,
        // it recalculates it automatically like this:
        //   Available = Total entered in form - how many copies are actually borrowed right now
        //
        // This prevents the count from getting wrong if someone changed the total while
        // copies were already borrowed.
        // If a new cover image file is uploaded, it replaces the old one.
        [HttpPost]
        public async Task<IActionResult> Edit(BookFormViewModel model, IFormFile? coverImageFile)
        {
            // Same duplicate-title guard as Register, but excluding this book's own
            // existing record so saving without changing the title doesn't false-positive.
            bool titleAlreadyExists = await _context.Books.AnyAsync(book =>
                book.Id != model.Id && !book.IsArchived && book.Title.ToLower() == model.Title.ToLower());
            if (titleAlreadyExists)
            {
                ModelState.AddModelError(nameof(model.Title),
                    $"\"{model.Title}\" has already been registered. Please add another book.");
            }

            if (!ModelState.IsValid) return View(model);

            var book = await _context.Books.FindAsync(model.Id);
            if (book == null) return NotFound();

            book.Title         = model.Title;
            book.Author        = model.Author;
            book.Category      = model.Category;
            book.GradeLevel    = model.GradeLevel;
            book.CallNumber    = model.CallNumber;
            book.AisleLocation = model.AisleLocation;
            book.Description   = model.Description;
            book.PublishedYear = model.PublishedYear;
            book.ISBN          = model.ISBN;
            book.BookFor       = model.BookFor;
            book.IsRoomUseOnly = model.IsRoomUseOnly;

            // If a new cover file was uploaded, save it and replace the URL.
            // Otherwise keep the existing cover image URL from the hidden field.
            if (coverImageFile != null && coverImageFile.Length > 0)
                book.CoverImageUrl = await SaveCoverImageAsync(coverImageFile);
            else
                book.CoverImageUrl = model.CoverImageUrl ?? "";

            // Count how many copies are currently borrowed (status = PickedUp)
            int copiesCurrentlyBorrowed = await _context.BookReservations
                .CountAsync(reservation => reservation.BookId == book.Id && reservation.Status == "PickedUp");

            book.TotalQuantity     = model.TotalQuantity;
            book.AvailableQuantity = Math.Max(0, model.TotalQuantity - copiesCurrentlyBorrowed);

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book updated successfully.";
            return RedirectToAction("Index");
        }

        // Lets the librarian upload an Excel file to add many books at once.
        // The Excel file should have headers in row 1 and book data starting from row 2.
        // Column order: Title | Author | Category | Grade Level | Call Number | Aisle | Year | Quantity | ISBN
        //
        // After importing, it sends the librarian to a review page where they can
        // add cover images and descriptions to the newly imported books.
        [HttpPost]
        public async Task<IActionResult> ImportExcel(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select an Excel file.";
                return RedirectToAction("Register");
            }

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            var importedIds = new List<int>();

            using var stream  = new MemoryStream();
            await file.CopyToAsync(stream);
            using var package = new ExcelPackage(stream);

            var sheet = package.Workbook.Worksheets.FirstOrDefault();
            if (sheet == null)
            {
                TempData["Error"] = "Excel file has no worksheets.";
                return RedirectToAction("Register");
            }

            int lastRow = sheet.Dimension?.End.Row ?? 0;

            // Start from row 2 because row 1 is the header
            for (int row = 2; row <= lastRow; row++)
            {
                string title = sheet.Cells[row, 1].Text.Trim();

                // Skip blank rows
                if (string.IsNullOrEmpty(title)) continue;

                // Parse quantity — default to 1 if the cell is empty or not a number
                bool quantityParsed = int.TryParse(sheet.Cells[row, 8].Text.Trim(), out int quantity);
                int  bookQuantity   = quantityParsed ? quantity : 1;

                var newBook = new Book
                {
                    Title             = title,
                    Author            = sheet.Cells[row, 2].Text.Trim(),
                    Category          = sheet.Cells[row, 3].Text.Trim(),
                    GradeLevel        = sheet.Cells[row, 4].Text.Trim(),
                    CallNumber        = sheet.Cells[row, 5].Text.Trim(),
                    AisleLocation     = sheet.Cells[row, 6].Text.Trim(),
                    PublishedYear     = sheet.Cells[row, 7].Text.Trim(),
                    TotalQuantity     = bookQuantity,
                    AvailableQuantity = bookQuantity,
                    ISBN              = sheet.Cells[row, 9].Text.Trim(),
                };

                _context.Books.Add(newBook);
                await _context.SaveChangesAsync();
                importedIds.Add(newBook.Id); // Keep track of which books were just imported
            }

            if (!importedIds.Any())
            {
                TempData["Error"] = "No valid rows found. Make sure row 1 is a header and data starts on row 2.";
                return RedirectToAction("Register");
            }

            // Pass the list of imported book IDs to the next page using TempData
            TempData["ImportedIds"] = string.Join(",", importedIds);
            TempData["Success"]     = $"{importedIds.Count} book(s) imported. Review and complete the details below.";
            return RedirectToAction("ImportReview");
        }

        // Shows the review table after an Excel import.
        // The librarian can add cover image URLs and descriptions to each imported book here.
        // TempData.Keep() is called so the list of book IDs doesn't disappear on page refresh.
        public async Task<IActionResult> ImportReview()
        {
            string idString = TempData["ImportedIds"] as string ?? "";
            if (string.IsNullOrEmpty(idString))
                return RedirectToAction("Index");

            TempData.Keep("ImportedIds"); // Keep the IDs so refreshing the page still works

            var ids   = idString.Split(',').Select(int.Parse).ToList();
            var books = await _context.Books
                .Where(book => ids.Contains(book.Id))
                .OrderBy(book => book.Title)
                .ToListAsync();

            return View(books);
        }

        // Called by the review page (via JavaScript) to save the cover image and description
        // for one book without needing to reload the whole page.
        [HttpPost]
        public async Task<IActionResult> UpdateBookDetails(int id, string? coverImageUrl, string? description)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            book.CoverImageUrl = coverImageUrl ?? "";
            book.Description   = description   ?? "";
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // Shows a printable shelf label for a single book (call number, title, aisle location).
        public async Task<IActionResult> PrintLabel(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();
            return View(book);
        }

        // Shows printable shelf labels for multiple books at once.
        // The `ids` parameter is a comma-separated list of book IDs.
        public async Task<IActionResult> PrintLabels(string ids)
        {
            if (string.IsNullOrEmpty(ids)) return BadRequest();

            var idList = ids.Split(',')
                .Select(part => int.TryParse(part.Trim(), out int parsedId) ? parsedId : 0)
                .Where(id => id > 0)
                .ToList();

            var books = await _context.Books.Where(book => idList.Contains(book.Id)).ToListAsync();
            return View(books);
        }

        // Hides a book from the catalog (sets it as archived with a reason).
        // Archived books won't show up in the student catalog or the librarian's available list.
        // Sets available quantity to 0 so no new borrows can happen.
        [HttpPost]
        public async Task<IActionResult> Archive(int id, string reason)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            book.IsArchived        = true;
            book.ArchiveReason     = reason;
            book.AvailableQuantity = 0;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book archived.";
            return RedirectToAction("Index");
        }

        // Shows the list of archived books along with summary stats.
        public async Task<IActionResult> Archive()
        {
            // Same stat card rules as Register page
            ViewBag.TotalBooks     = await _context.Books.Where(book => !book.IsArchived).SumAsync(book => (int?)book.TotalQuantity) ?? 0;
            ViewBag.BorrowedBooks  = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp" || r.Status == "Overdue");
            ViewBag.AvailableBooks = (int)ViewBag.TotalBooks - (int)ViewBag.BorrowedBooks;
            ViewBag.ArchivedBooks  = await _context.Books.CountAsync(book => book.IsArchived);

            var archivedBooks = await _context.Books
                .Where(book => book.IsArchived)
                .OrderByDescending(book => book.CreatedAt)
                .ToListAsync();

            return View(archivedBooks);
        }

        // Brings an archived book back to the active catalog.
        // Note: Available quantity is not automatically restored here — the librarian should
        // edit the book afterward to set the correct number if some copies were lost.
        [HttpPost]
        public async Task<IActionResult> Restore(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            book.IsArchived    = false;
            book.ArchiveReason = "";

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book restored.";
            return RedirectToAction("Archive");
        }

        // Generates a QR code image for a book's shelf location.
        // When scanned, the QR code shows the book's title, call number, and aisle.
        // This is printed on the spine label so students can scan it with their phone.
        public async Task<IActionResult> QRCode(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            // The content that gets encoded inside the QR code
            string qrContent = $"BookHive\nTitle: {book.Title}\nCall No: {book.CallNumber}\nAisle: {book.AisleLocation}";

            using var qrGenerator = new QRCodeGenerator();
            var qrData    = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrData);
            byte[] pngImage = qrCode.GetGraphic(6); // 6 = pixel size per square in the QR grid

            return File(pngImage, "image/png");
        }
    }
}
