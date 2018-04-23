using Microsoft.AspNetCore.Mvc.Rendering;

namespace gcbulkgrader.Models
{
    public class CourseSelectionModel : PersonModel
    {
        public SelectList Courses { get; set; }
    }
}
