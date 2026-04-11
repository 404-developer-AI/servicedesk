-- Servicedesk dev database bootstrap
-- Run once as a PostgreSQL superuser on your local machine.
-- Replace the password below before running. Do NOT commit the real password.
--
-- psql -U postgres -f db/init/000_dev_setup.sql

CREATE ROLE servicedesk_app WITH LOGIN PASSWORD 'CHANGE_ME_LOCAL_DEV';
CREATE DATABASE servicedesk_dev WITH OWNER servicedesk_app ENCODING 'UTF8';

\connect servicedesk_dev

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO servicedesk_app;
