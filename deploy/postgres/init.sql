CREATE USER "catalog-api" WITH PASSWORD 'catalog-api';
CREATE DATABASE "Catalog" OWNER "catalog-api";
GRANT ALL PRIVILEGES ON DATABASE "Catalog" TO "catalog-api";

CREATE USER "orders-api" WITH PASSWORD 'orders-api';
CREATE DATABASE "Orders" OWNER "orders-api";
GRANT ALL PRIVILEGES ON DATABASE "Orders" TO "orders-api";

CREATE USER "inventory-api" WITH PASSWORD 'inventory-api';
CREATE DATABASE "Inventory" OWNER "inventory-api";
GRANT ALL PRIVILEGES ON DATABASE "Inventory" TO "inventory-api";
