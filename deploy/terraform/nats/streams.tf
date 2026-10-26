resource "jetstream_stream" "streams" {
  for_each = local.streams

  name      = each.value.name
  subjects  = each.value.subjects
  retention = each.value.retention
  storage   = "file"
  replicas  = 1
}
