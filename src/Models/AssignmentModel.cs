namespace gcbulkgrader.Models
{
    public class AssignmentModel
    {
        public string CourseId { get; set; }
        public string CourseName { get; set; }
        public string CourseWorkId { get; set; }
        public string CourseWorkName { get; set; }
        public double MaxPoints { get; set; }
    }
}
